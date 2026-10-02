using BuildingOs.ApiServer.Health;
using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Infrastructure.HealthEvents;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.ApiServer.Test;

public class HealthEvaluationCycleTest
{
    private static readonly DateTime T = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeStore : IHealthEventStore
    {
        public List<HealthEventEntry> Rows { get; } = [];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<HealthEventEntry>> ListOpenAsync(CancellationToken ct = default)
        { Calls++; return Task.FromResult<IReadOnlyList<HealthEventEntry>>(Rows.Where(r => r.IsOpen).ToArray()); }

        public Task<RaiseOutcome> RaiseAsync(HealthEventCandidate c, DateTime now, CancellationToken ct = default)
        {
            Calls++;
            if (Rows.Any(r => r.IsOpen && r.SubjectType == c.SubjectType && r.SubjectId == c.SubjectId && r.Kind == c.Kind))
                return Task.FromResult(RaiseOutcome.AlreadyOpen);
            Rows.Add(new HealthEventEntry
            {
                Id = Guid.NewGuid(), SubjectType = c.SubjectType, SubjectId = c.SubjectId, Kind = c.Kind,
                Severity = c.Severity, RaisedAt = now, Detail = c.DetailJson,
            });
            return Task.FromResult(RaiseOutcome.Raised);
        }

        public Task UpdateOpenAsync(string t, string s, string k, string sev, string d, CancellationToken ct = default)
        {
            Calls++;
            foreach (var r in Rows.Where(r => r.IsOpen && r.SubjectType == t && r.SubjectId == s && r.Kind == k)) { r.Severity = sev; r.Detail = d; }
            return Task.CompletedTask;
        }

        public Task<int> ClearAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken ct = default)
        {
            Calls++;
            var n = 0;
            foreach (var r in Rows.Where(r => r.IsOpen && ids.Contains(r.Id))) { r.ClearedAt = now; n++; }
            return Task.FromResult(n);
        }

        public Task<HealthEventPage> QueryAsync(HealthEventQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HealthEventEntry?> GetAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HealthEventEntry?> AcknowledgeAsync(Guid id, string s, string? n, DateTime now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PruneClearedAsync(DateTime before, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static PointHealthItem Item(string id, FreshnessStatus fresh = FreshnessStatus.Fresh, string gw = "GW-1", bool? connected = true) => new()
    {
        PointId = id,
        Freshness = new PointFreshnessResult { Status = fresh, ThresholdSeconds = 300, AgeSeconds = 900 },
        Alarm = new PointAlarmResult { Status = AlarmStatus.Normal },
        Gateway = new PointGatewayInfo(gw, connected),
    };

    private static PointHealthSnapshot Snap(PointLastSeenIndexState state, params PointHealthItem[] items) => new([.. items], state);

    private static HealthEvaluationCycle Cycle(int raise = 1, int clear = 1) =>
        new(new HealthEventReconciler(new HealthEventHysteresis(raise, clear)));

    private static Task<HealthEvaluationResult> Run(HealthEvaluationCycle c, FakeStore s, PointHealthSnapshot snap) =>
        c.RunAsync(snap, s, T, NullLogger.Instance, default);

    [Theory]
    [InlineData(PointLastSeenIndexState.Warming)]
    [InlineData(PointLastSeenIndexState.Degraded)]
    public async Task IndexNotReady_SkipsTheWholeScan_AndLeavesOpenEventsAlone(PointLastSeenIndexState state)
    {
        var store = new FakeStore();
        store.Rows.Add(new HealthEventEntry { Id = Guid.NewGuid(), SubjectType = "point", SubjectId = "P1", Kind = "stale", Severity = "warn", RaisedAt = T });

        // Everything looks Unknown/Fresh here — acting on it would clear the real event.
        var r = await Run(Cycle(), store, Snap(state, Item("P1")));

        Assert.True(r.Skipped);
        Assert.Equal(0, store.Calls);
        Assert.True(store.Rows.Single().IsOpen);
    }

    [Fact]
    public async Task StaleThenRecovered_RaisesOneEvent_ThenClearsIt()
    {
        var store = new FakeStore();
        var cycle = Cycle();

        var raised = await Run(cycle, store, Snap(PointLastSeenIndexState.Ready, Item("P1", FreshnessStatus.Stale)));
        Assert.Equal(1, raised.Raised);
        Assert.Single(store.Rows, e => e.IsOpen);

        var again = await Run(cycle, store, Snap(PointLastSeenIndexState.Ready, Item("P1", FreshnessStatus.Stale)));
        Assert.Equal(0, again.Raised); // still the one open event

        var cleared = await Run(cycle, store, Snap(PointLastSeenIndexState.Ready, Item("P1")));
        Assert.Equal(1, cleared.Cleared);
        Assert.Empty(store.Rows.Where(e => e.IsOpen));
    }

    private sealed class LosesTheRaceStore : FakeStore2
    {
        // Another replica opened the same condition between this scan's read and its write.
        public override Task<RaiseOutcome> RaiseAsync(HealthEventCandidate c, DateTime now, CancellationToken ct = default) =>
            Task.FromResult(RaiseOutcome.AlreadyOpen);
    }

    // FakeStore is sealed above for brevity; this thin subclassable copy only needs the read path.
    private abstract class FakeStore2 : IHealthEventStore
    {
        public Task<IReadOnlyList<HealthEventEntry>> ListOpenAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HealthEventEntry>>([]);
        public abstract Task<RaiseOutcome> RaiseAsync(HealthEventCandidate c, DateTime now, CancellationToken ct = default);
        public Task UpdateOpenAsync(string t, string s, string k, string sev, string d, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> ClearAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken ct = default) => Task.FromResult(0);
        public Task<HealthEventPage> QueryAsync(HealthEventQuery q, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HealthEventEntry?> GetAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HealthEventEntry?> AcknowledgeAsync(Guid id, string s, string? n, DateTime now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PruneClearedAsync(DateTime before, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task LosingTheRaceToAnotherReplica_IsCountedAsConverged_NotAsARaise()
    {
        var r = await Cycle().RunAsync(
            Snap(PointLastSeenIndexState.Ready, Item("P1", FreshnessStatus.Stale)), new LosesTheRaceStore(), T, NullLogger.Instance, default);

        Assert.Equal(0, r.Raised);
        Assert.Equal(1, r.AlreadyOpen);
    }

    [Fact]
    public async Task GatewayOutage_CollapsesToOneEvent_AndRecoveryClearsIt()
    {
        var store = new FakeStore();
        var cycle = Cycle();
        var down = Enumerable.Range(0, 200).Select(i => Item($"P{i}", FreshnessStatus.Missing, connected: false)).ToArray();

        await Run(cycle, store, Snap(PointLastSeenIndexState.Ready, down));

        var only = Assert.Single(store.Rows);
        Assert.Equal(("gateway", "GW-1", "gateway_offline"), (only.SubjectType, only.SubjectId, only.Kind));

        var back = Enumerable.Range(0, 200).Select(i => Item($"P{i}")).ToArray();
        var r = await Run(cycle, store, Snap(PointLastSeenIndexState.Ready, back));
        Assert.Equal(1, r.Cleared);
    }
}
