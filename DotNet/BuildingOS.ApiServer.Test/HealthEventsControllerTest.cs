using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Health;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Infrastructure.HealthEvents;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// #455 API: the list is filtered by lifecycle and acknowledgement as two separate axes, an event is only visible
/// to someone who can read its subject, and acknowledging is idempotent, role-gated, and audited once.
/// </summary>
public class HealthEventsControllerTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class EmptyIndex : IPointLastSeenIndex
    {
        public PointLastSeenIndexState State => PointLastSeenIndexState.Ready;
        public DateTimeOffset? LastSyncAt => null;
        public int Count => 0;
        public bool TryGet(string pointId, out PointLastSeenEntry entry) { entry = null!; return false; }
    }

    private static HealthEventEntry Event(string type, string subject, string kind = "stale", DateTime? cleared = null, DateTime? acked = null) => new()
    {
        Id = Guid.NewGuid(), SubjectType = type, SubjectId = subject, Kind = kind, Severity = "warn",
        RaisedAt = Now.UtcDateTime.AddHours(-1), ClearedAt = cleared, AcknowledgedAt = acked,
        AcknowledgedBy = acked is null ? null : "sub-1", Detail = """{"ageSeconds":900}""",
    };

    private static PointDetail Detail(string id, string gateway) => new()
    {
        Point = new Point { DtId = "urn:dtid:" + id, Id = id, Name = "名前-" + id, GatewayName = gateway },
        Device = new Device { DtId = "urn:dtid:d", Id = "D", Name = "AHU", BuildingName = "本館" },
    };

    private sealed record Harness(
        HealthEventsController Controller, Mock<IHealthEventStore> Store, Mock<IAdminAuditRecorder> Audit, Mock<IAuthorizedTwinView> View);

    /// <summary><paramref name="readable"/>: the points the (non-admin) caller can read, as (pointId, gatewayId).</summary>
    private static Harness Build(string role, (string Point, string Gateway)[]? readable = null, string sub = "u1")
    {
        var store = new Mock<IHealthEventStore>();
        store.Setup(s => s.QueryAsync(It.IsAny<HealthEventQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthEventPage([], 0));

        var details = (readable ?? []).Select(r => Detail(r.Point, r.Gateway)).ToArray();
        var view = new Mock<IAuthorizedTwinView>();
        view.Setup(v => v.ListBuildingsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Building { DtId = "urn:b", Id = "b", Name = "本館" }]);
        view.Setup(v => v.ListPointDetailsAsync(It.IsAny<AuthorizationContext>(), "urn:b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetTelemetryThresholdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TelemetryThresholds(300, 3));
        var gateways = new Mock<IGatewayConnectionStatusStore>();
        gateways.Setup(g => g.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(GatewayConnectionLookup.Disconnected);
        var ledger = new PointHealthLedger(view.Object, new EmptyIndex(), settings.Object, gateways.Object, new FixedClock(Now));

        var audit = new Mock<IAdminAuditRecorder>();
        var controller = new HealthEventsController(store.Object, ledger, new FixedClock(Now), NullLogger<HealthEventsController>.Instance, audit.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items = { ["AuthorizationContext"] = new AuthorizationContext { UserId = sub, Role = role, Permissions = [] } },
                },
            },
        };
        return new Harness(controller, store, audit, view);
    }

    private static HealthEventQuery Captured(Harness h) =>
        (HealthEventQuery)h.Store.Invocations.Last(i => i.Method.Name == nameof(IHealthEventStore.QueryAsync)).Arguments[0];

    // ── list ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ForwardsLifecycleAndAck_AsSeparateFilters()
    {
        var h = Build("admin");

        await h.Controller.List("open", "unacked", ["stale", "ALARM"], "point", " P1 ", Now.AddDays(-1), 20, 5);

        var q = Captured(h);
        Assert.Equal(HealthEventLifecycle.Open, q.Lifecycle);
        Assert.Equal(HealthEventAckFilter.Unacked, q.Ack);
        Assert.Equal(["stale", "alarm"], q.Kinds);
        Assert.Equal(("point", "P1", 20, 5), (q.SubjectType, q.SubjectId, q.Limit, q.Offset));
        Assert.Equal(Now.AddDays(-1).UtcDateTime, q.Since);
    }

    [Fact]
    public async Task List_Admin_IsUnrestricted()
    {
        var h = Build("admin");
        await h.Controller.List(null, null, null, null, null, null);
        Assert.Null(Captured(h).Scope);
    }

    [Fact]
    public async Task List_Operator_IsScopedToTheReadablePointsAndTheirGateways()
    {
        var h = Build("operator", [("P1", "GW-1"), ("P2", "GW-1"), ("P3", "GW-2")]);

        await h.Controller.List(null, null, null, null, null, null);

        var scope = Captured(h).Scope!;
        Assert.Equivalent(new[] { "P1", "P2", "P3" }, scope.PointIds);
        Assert.Equivalent(new[] { "GW-1", "GW-2" }, scope.GatewayIds);
    }

    [Fact]
    public async Task List_GroupManager_GetsNothing_AndTheStoreIsNotAsked()
    {
        var h = Build("group-manager");

        var result = await h.Controller.List(null, null, null, null, null, null);

        Assert.Empty(((HealthEventListResponse)((OkObjectResult)result.Result!).Value!).Items);
        h.Store.Verify(s => s.QueryAsync(It.IsAny<HealthEventQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("opne", null, null, null)]
    [InlineData(null, "maybe", null, null)]
    [InlineData(null, null, "stale,missing", null)]
    [InlineData(null, null, null, "building")]
    public async Task List_RefusesAnUnknownFilterValue_RatherThanWideningTheList(string? life, string? ack, string? kind, string? subject)
    {
        var h = Build("admin");

        var result = await h.Controller.List(life, ack, kind is null ? null : [kind], subject, null, null);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        h.Store.Verify(s => s.QueryAsync(It.IsAny<HealthEventQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task List_NegativeOffset_IsRefused_AndLimitIsClamped()
    {
        var h = Build("admin");
        Assert.IsType<BadRequestObjectResult>((await h.Controller.List(null, null, null, null, null, null, 10, -1)).Result);

        await h.Controller.List(null, null, null, null, null, null, 99999, 0);
        Assert.Equal(500, Captured(h).Limit);
    }

    [Fact]
    public async Task List_ShowsNamesForPoints_AndKeepsLifecycleAndAckApart()
    {
        var h = Build("operator", [("P1", "GW-1")]);
        var open = Event("point", "P1");
        var clearedAcked = Event("point", "P1", "alarm", cleared: Now.UtcDateTime, acked: Now.UtcDateTime);
        h.Store.Setup(s => s.QueryAsync(It.IsAny<HealthEventQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthEventPage([open, clearedAcked], 2));

        var body = (HealthEventListResponse)((OkObjectResult)(await h.Controller.List(null, null, null, null, null, null)).Result!).Value!;

        Assert.Equal("名前-P1", body.Items[0].SubjectName);
        Assert.Equal("本館", body.Items[0].BuildingName);
        Assert.True(body.Items[0].IsOpen);
        Assert.Null(body.Items[0].AcknowledgedAt);
        Assert.False(body.Items[1].IsOpen);
        Assert.NotNull(body.Items[1].AcknowledgedAt);       // cleared AND acknowledged
        Assert.Equal(900L, body.Items[0].Detail.AgeSeconds);
        Assert.Equal(DateTimeKind.Utc, body.Items[0].RaisedAt.Kind);
    }

    // ── acknowledge ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Ack_RecordsTheCaller_AndAuditsTheFirstAcknowledgementOnly()
    {
        var h = Build("operator", [("P1", "GW-1")], sub: "op-9");
        var e = Event("point", "P1");
        h.Store.Setup(s => s.GetAsync(e.Id, It.IsAny<CancellationToken>())).ReturnsAsync(e);
        h.Store.Setup(s => s.AcknowledgeAsync(e.Id, "op-9", It.IsAny<string?>(), Now.UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthEventAckResult(new HealthEventEntry
            {
                Id = e.Id, SubjectType = "point", SubjectId = "P1", Kind = "stale", Severity = "warn", RaisedAt = e.RaisedAt,
                AcknowledgedAt = Now.UtcDateTime, AcknowledgedBy = "op-9", Detail = "{}",
            }, Applied: true));

        var result = await h.Controller.Acknowledge(e.Id);

        var body = (HealthEventResponse)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("op-9", body.AcknowledgedBy);
        h.Audit.Verify(a => a.RecordAsync(It.Is<AdminAuditRecord>(r =>
            r.SubjectType == "health-event" && r.Action == "acknowledge" && r.ActorSub == "op-9" && r.TargetId == e.Id.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ack_AnAlreadyAcknowledgedEvent_ReturnsItUnchanged_WithoutAuditingAgain()
    {
        var h = Build("admin", sub: "admin-2");
        var already = Event("point", "P1", acked: Now.UtcDateTime.AddMinutes(-5)); // acknowledged by sub-1
        h.Store.Setup(s => s.GetAsync(already.Id, It.IsAny<CancellationToken>())).ReturnsAsync(already);
        h.Store.Setup(s => s.AcknowledgeAsync(already.Id, "admin-2", It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthEventAckResult(already, Applied: false)); // the store keeps the first acknowledger

        var result = await h.Controller.Acknowledge(already.Id);

        Assert.Equal("sub-1", ((HealthEventResponse)((OkObjectResult)result.Result!).Value!).AcknowledgedBy);
        h.Audit.Verify(a => a.RecordAsync(It.IsAny<AdminAuditRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ack_ViewerCannot()
    {
        var h = Build("viewer", [("P1", "GW-1")]);

        Assert.IsType<ForbidResult>((await h.Controller.Acknowledge(Guid.NewGuid())).Result);
        h.Store.Verify(s => s.AcknowledgeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ack_GroupManagerCannot()
    {
        var h = Build("group-manager");
        Assert.IsType<ForbidResult>((await h.Controller.Acknowledge(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Ack_UnknownEvent_IsNotFound()
    {
        var h = Build("admin");
        h.Store.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((HealthEventEntry?)null);

        Assert.IsType<NotFoundResult>((await h.Controller.Acknowledge(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task Ack_AnEventOnSomethingTheCallerCannotRead_IsNotFound_NotForbidden()
    {
        var h = Build("operator", [("P1", "GW-1")]);
        var theirs = Event("point", "SECRET");
        h.Store.Setup(s => s.GetAsync(theirs.Id, It.IsAny<CancellationToken>())).ReturnsAsync(theirs);

        Assert.IsType<NotFoundResult>((await h.Controller.Acknowledge(theirs.Id)).Result);
        h.Store.Verify(s => s.AcknowledgeAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ack_AGatewayEvent_IsAllowedWhenTheCallerReadsOneOfItsPoints()
    {
        var h = Build("operator", [("P1", "GW-1")]);
        var gw = Event("gateway", "GW-1", "gateway_offline");
        h.Store.Setup(s => s.GetAsync(gw.Id, It.IsAny<CancellationToken>())).ReturnsAsync(gw);
        h.Store.Setup(s => s.AcknowledgeAsync(gw.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthEventAckResult(gw, Applied: true));

        Assert.IsType<OkObjectResult>((await h.Controller.Acknowledge(gw.Id)).Result);
    }

    [Fact]
    public async Task Ack_WhenAnotherCallerWonTheRace_IsNotAuditedAgain_EvenIfTheStoredAckerIsTheSameUser()
    {
        // Two requests from the same operator both read the event unacknowledged; the store's conditional
        // update applied only one. The loser's row says "acknowledged by me" too — it must still not audit.
        var h = Build("operator", [("P1", "GW-1")], sub: "op-9");
        var e = Event("point", "P1");
        h.Store.Setup(s => s.GetAsync(e.Id, It.IsAny<CancellationToken>())).ReturnsAsync(e);
        var acked = new HealthEventEntry
        {
            Id = e.Id, SubjectType = "point", SubjectId = "P1", Kind = "stale", Severity = "warn", RaisedAt = e.RaisedAt,
            AcknowledgedAt = Now.UtcDateTime, AcknowledgedBy = "op-9", Detail = "{}",
        };
        h.Store.Setup(s => s.AcknowledgeAsync(e.Id, "op-9", It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthEventAckResult(acked, Applied: false));

        await h.Controller.Acknowledge(e.Id);

        h.Audit.Verify(a => a.RecordAsync(It.IsAny<AdminAuditRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ack_AGatewayEvent_DoesNotRebuildTheLedgerForAnAdmin()
    {
        var h = Build("admin");
        var gw = Event("gateway", "GW-1", "gateway_offline");
        h.Store.Setup(s => s.GetAsync(gw.Id, It.IsAny<CancellationToken>())).ReturnsAsync(gw);
        h.Store.Setup(s => s.AcknowledgeAsync(gw.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthEventAckResult(gw, Applied: true));

        Assert.IsType<OkObjectResult>((await h.Controller.Acknowledge(gw.Id)).Result);

        h.View.Verify(v => v.ListBuildingsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ack_ALedgerFailureAfterTheCommit_StillReturnsTheEvent_Unnamed()
    {
        var h = Build("admin");
        var e = Event("point", "P1");
        h.Store.Setup(s => s.GetAsync(e.Id, It.IsAny<CancellationToken>())).ReturnsAsync(e);
        h.Store.Setup(s => s.AcknowledgeAsync(e.Id, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthEventAckResult(e, Applied: true));
        h.View.Setup(v => v.ListBuildingsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("twin down"));

        var body = (HealthEventResponse)((OkObjectResult)(await h.Controller.Acknowledge(e.Id)).Result!).Value!;

        Assert.Null(body.SubjectName);
    }

    [Fact]
    public async Task List_ForAnAdmin_StillAnswers_WhenTheNameLookupFails()
    {
        var h = Build("admin");
        h.Store.Setup(s => s.QueryAsync(It.IsAny<HealthEventQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthEventPage([Event("point", "P1")], 1));
        h.View.Setup(v => v.ListBuildingsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("twin down"));

        var body = (HealthEventListResponse)((OkObjectResult)(await h.Controller.List(null, null, null, null, null, null)).Result!).Value!;

        var item = Assert.Single(body.Items);
        Assert.Null(item.SubjectName);
        Assert.Equal("P1", item.SubjectId);
    }
}
