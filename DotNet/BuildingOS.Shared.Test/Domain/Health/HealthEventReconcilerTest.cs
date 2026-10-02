using BuildingOS.Shared.Domain.Health;

namespace BuildingOS.Shared.Test.Domain.Health;

public class HealthEventReconcilerTest
{
    private static PointHealthItem Item(
        string id, FreshnessStatus fresh = FreshnessStatus.Fresh, AlarmStatus alarm = AlarmStatus.Normal,
        string? gateway = "GW-1", bool? connected = true, MissingReason? reason = null) => new()
        {
            PointId = id,
            Freshness = new PointFreshnessResult { Status = fresh, ThresholdSeconds = 300, AgeSeconds = 900, Reason = reason },
            Alarm = new PointAlarmResult { Status = alarm, Value = 42, Violated = alarm == AlarmStatus.Normal ? null : AlarmBound.AlarmHigh },
            Gateway = gateway is null ? null : new PointGatewayInfo(gateway, connected),
        };

    private static HealthEventEntry Open(string type, string subject, string kind, string severity = "warn") => new()
    {
        Id = Guid.NewGuid(), SubjectType = type, SubjectId = subject, Kind = kind, Severity = severity,
        RaisedAt = DateTime.UtcNow,
    };

    private static HealthEventReconciler Now(int raiseAfter = 1, int clearAfter = 1) =>
        new(new HealthEventHysteresis(raiseAfter, clearAfter));

    private static readonly IReadOnlyList<HealthEventEntry> NoOpen = [];

    // ── raise ────────────────────────────────────────────────────────────────

    [Fact]
    public void Stale_Missing_AndAlarm_RaiseOnePointEventEach_WithSeverity()
    {
        var plan = Now().Reconcile(
        [
            Item("P-STALE", FreshnessStatus.Stale),
            Item("P-MISS", FreshnessStatus.Missing, reason: MissingReason.NeverReceived),
            Item("P-ALARM", alarm: AlarmStatus.Critical),
            Item("P-WARN", alarm: AlarmStatus.Warn),
            Item("P-OK"),
        ], NoOpen);

        var by = plan.Raise.ToDictionary(c => (c.SubjectId, c.Kind), c => c.Severity);
        Assert.Equal(4, plan.Raise.Count);
        Assert.Equal("warn", by[("P-STALE", "stale")]);
        Assert.Equal("warn", by[("P-MISS", "missing")]);
        Assert.Equal("critical", by[("P-ALARM", "alarm")]);
        Assert.Equal("warn", by[("P-WARN", "alarm")]);
        Assert.All(plan.Raise, c => Assert.Equal("point", c.SubjectType));
    }

    [Fact]
    public void Detail_CarriesTheSnapshot()
    {
        var plan = Now().Reconcile([Item("P1", FreshnessStatus.Stale), Item("P2", alarm: AlarmStatus.Critical)], NoOpen);

        Assert.Contains("900", plan.Raise.Single(c => c.Kind == "stale").DetailJson);
        var alarm = plan.Raise.Single(c => c.Kind == "alarm").DetailJson;
        Assert.Contains("42", alarm);
        Assert.Contains("AlarmHigh", alarm);
    }

    [Fact]
    public void AnAlreadyOpenCondition_IsNotRaisedAgain()
    {
        var plan = Now().Reconcile([Item("P1", FreshnessStatus.Stale)], [Open("point", "P1", "stale")]);

        Assert.Empty(plan.Raise);
        Assert.Empty(plan.Clear);
    }

    [Fact]
    public void FreshAndNormalPoints_RaiseNothing()
    {
        Assert.Equal(HealthEventPlan.Empty.Raise, Now().Reconcile([Item("P1"), Item("P2")], NoOpen).Raise);
    }

    // ── hysteresis ───────────────────────────────────────────────────────────

    [Fact]
    public void Raise_NeedsConsecutiveScans()
    {
        var r = Now(raiseAfter: 3);
        var stale = new[] { Item("P1", FreshnessStatus.Stale) };

        Assert.Empty(r.Reconcile(stale, NoOpen).Raise);
        Assert.Empty(r.Reconcile(stale, NoOpen).Raise);
        Assert.Single(r.Reconcile(stale, NoOpen).Raise);
    }

    [Fact]
    public void ABlip_RestartsTheRaiseCount()
    {
        var r = Now(raiseAfter: 2);
        r.Reconcile([Item("P1", FreshnessStatus.Stale)], NoOpen);
        r.Reconcile([Item("P1")], NoOpen); // recovered for one scan

        Assert.Empty(r.Reconcile([Item("P1", FreshnessStatus.Stale)], NoOpen).Raise); // counting from 1 again
        Assert.Single(r.Reconcile([Item("P1", FreshnessStatus.Stale)], NoOpen).Raise);
    }

    [Fact]
    public void Clear_NeedsConsecutiveCleanScans_AndALapseResetsTheCount()
    {
        var r = Now(clearAfter: 3);
        var e = Open("point", "P1", "stale");
        var fresh = new[] { Item("P1") };

        Assert.Empty(r.Reconcile(fresh, [e]).Clear);
        Assert.Empty(r.Reconcile(fresh, [e]).Clear);
        r.Reconcile([Item("P1", FreshnessStatus.Stale)], [e]);          // it came back: the count restarts
        Assert.Empty(r.Reconcile(fresh, [e]).Clear);
        Assert.Empty(r.Reconcile(fresh, [e]).Clear);
        Assert.Equal([e.Id], r.Reconcile(fresh, [e]).Clear);
    }

    // ── clear / unknown ──────────────────────────────────────────────────────

    [Fact]
    public void StaleBecomingMissing_ClearsTheStaleEvent_AndRaisesMissing()
    {
        var e = Open("point", "P1", "stale");

        var plan = Now().Reconcile([Item("P1", FreshnessStatus.Missing, reason: MissingReason.NeverReceived)], [e]);

        Assert.Equal([e.Id], plan.Clear);
        Assert.Equal("missing", Assert.Single(plan.Raise).Kind);
    }

    [Fact]
    public void UnknownFreshness_NeitherRaisesNorClears()
    {
        var e = Open("point", "P1", "stale");

        var plan = Now().Reconcile([Item("P1", FreshnessStatus.Unknown)], [e]);

        Assert.Empty(plan.Raise);
        Assert.Empty(plan.Clear);
    }

    [Theory]
    [InlineData(AlarmStatus.Unknown)]
    [InlineData(AlarmStatus.Suppressed)]
    public void AnAlarm_OnlyClearsWhenTheValueIsObservablyNormal(AlarmStatus status)
    {
        var e = Open("point", "P1", "alarm", "critical");

        Assert.Empty(Now().Reconcile([Item("P1", alarm: status)], [e]).Clear);
        Assert.Equal([e.Id], Now().Reconcile([Item("P1", alarm: AlarmStatus.Normal)], [e]).Clear);
    }

    [Fact]
    public void AlarmSeverityChange_UpdatesTheOpenEvent_InsteadOfRaisingANewOne()
    {
        var e = Open("point", "P1", "alarm", "warn");

        var plan = Now().Reconcile([Item("P1", alarm: AlarmStatus.Critical)], [e]);

        Assert.Empty(plan.Raise);
        var u = Assert.Single(plan.Update);
        Assert.Equal("critical", u.Severity);
        Assert.Equal(new HealthEventKey("point", "P1", "alarm"), u.Key);
    }

    [Fact]
    public void ASamePointNoLongerInTheTwin_ClearsItsEvents()
    {
        var e = Open("point", "GONE", "stale");

        Assert.Equal([e.Id], Now().Reconcile([Item("P1")], [e]).Clear);
    }

    // ── gateway / storm ──────────────────────────────────────────────────────

    [Fact]
    public void OfflineGateway_RaisesOneGatewayEvent_NotOnePerPoint()
    {
        var items = Enumerable.Range(0, 500)
            .Select(i => Item($"P{i}", i % 2 == 0 ? FreshnessStatus.Stale : FreshnessStatus.Missing, connected: false,
                reason: MissingReason.GatewayDisconnected))
            .ToArray();

        var plan = Now().Reconcile(items, NoOpen);

        var e = Assert.Single(plan.Raise);
        Assert.Equal(("gateway", "GW-1", "gateway_offline", "critical"), (e.SubjectType, e.SubjectId, e.Kind, e.Severity));
        Assert.Contains("500", e.DetailJson);
    }

    [Fact]
    public void OnlyTheOfflineGatewaysPointsAreCollapsed()
    {
        var plan = Now().Reconcile(
        [
            Item("P-DOWN", FreshnessStatus.Missing, gateway: "GW-1", connected: false),
            Item("P-OTHER", FreshnessStatus.Stale, gateway: "GW-2", connected: true),
        ], NoOpen);

        Assert.Equal(2, plan.Raise.Count);
        Assert.Contains(plan.Raise, c => c.Kind == "gateway_offline" && c.SubjectId == "GW-1");
        Assert.Contains(plan.Raise, c => c.Kind == "stale" && c.SubjectId == "P-OTHER");
    }

    [Fact]
    public void WhileTheGatewayIsDown_ItsPointsEventsAreHeld_NotCleared()
    {
        var stale = Open("point", "P1", "stale");
        var alarm = Open("point", "P1", "alarm", "critical");
        var down = new[] { Item("P1", FreshnessStatus.Stale, connected: false) };

        var plan = Now().Reconcile(down, [stale, alarm, Open("gateway", "GW-1", "gateway_offline", "critical")]);

        Assert.Empty(plan.Clear);
    }

    [Fact]
    public void WhenTheGatewayReturns_EventsClearOnTheNormalSchedule()
    {
        var gw = Open("gateway", "GW-1", "gateway_offline", "critical");
        var stale = Open("point", "P1", "stale");
        var r = Now(clearAfter: 2);
        var back = new[] { Item("P1") };

        Assert.Empty(r.Reconcile(back, [gw, stale]).Clear);
        Assert.Equivalent(new[] { gw.Id, stale.Id }, r.Reconcile(back, [gw, stale]).Clear);
    }

    [Fact]
    public void UnknownGatewayState_IsNotTreatedAsOffline()
    {
        var plan = Now().Reconcile([Item("P1", FreshnessStatus.Stale, connected: null)], NoOpen);

        var e = Assert.Single(plan.Raise);
        Assert.Equal("stale", e.Kind); // a per-point event, no gateway_offline
    }

    [Fact]
    public void AGatewayEvent_WithNoPointsLeft_Clears_ButUnknownState_Holds()
    {
        var gw = Open("gateway", "GW-OLD", "gateway_offline", "critical");
        Assert.Equal([gw.Id], Now().Reconcile([Item("P1", gateway: "GW-1")], [gw]).Clear);

        var gw2 = Open("gateway", "GW-1", "gateway_offline", "critical");
        Assert.Empty(Now().Reconcile([Item("P1", gateway: "GW-1", connected: null)], [gw2]).Clear);
    }

    [Fact]
    public void ApointWithNoGateway_StillGetsItsOwnEvents()
    {
        var plan = Now().Reconcile([Item("P1", FreshnessStatus.Stale, gateway: null)], NoOpen);

        Assert.Equal("stale", Assert.Single(plan.Raise).Kind);
    }

    [Fact]
    public void ARepeatedRun_OverTheSameInput_IsStable()
    {
        var r = Now();
        var items = new[] { Item("P1", FreshnessStatus.Stale), Item("P2", alarm: AlarmStatus.Warn) };
        var firstRaise = r.Reconcile(items, NoOpen).Raise;
        // the store now holds what the first plan raised
        var open = firstRaise.Select(c => Open(c.SubjectType, c.SubjectId, c.Kind, c.Severity)).ToArray();

        var second = r.Reconcile(items, open);

        Assert.Empty(second.Raise);
        Assert.Empty(second.Update);
        Assert.Empty(second.Clear);
    }
}
