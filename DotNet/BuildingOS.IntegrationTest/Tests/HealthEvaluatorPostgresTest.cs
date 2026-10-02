using BuildingOs.ApiServer.Health;
using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Infrastructure.HealthEvents;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #455 acceptance, against real PostgreSQL: a point goes stale → exactly one open event → recovers → it is
/// cleared; a gateway outage is one event, not one per point; and two evaluator replicas scanning the same
/// ledger leave one open event. The snapshot is injected, so no NATS is needed — the unit under test is the
/// cycle (reconciler + store), not the ledger assembly.
/// </summary>
[Collection(Names.Postgres)]
public class HealthEvaluatorPostgresTest(PostgresFixture postgres) : IntegrationTestBase
{
    private static readonly DateTime T = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private async Task<(EfHealthEventStore Store, RelationalDbContext Db)> StoreAsync()
    {
        var db = new RelationalDbContext(new DbContextOptionsBuilder<RelationalDbContext>().UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();
        return (new EfHealthEventStore(db), db);
    }

    private static PointHealthItem Item(string id, FreshnessStatus fresh, string gw, bool? connected = true) => new()
    {
        PointId = id,
        Freshness = new PointFreshnessResult { Status = fresh, ThresholdSeconds = 300, AgeSeconds = 900 },
        Alarm = new PointAlarmResult { Status = AlarmStatus.Normal },
        Gateway = new PointGatewayInfo(gw, connected),
    };

    private static HealthEvaluationCycle Cycle() => new(new HealthEventReconciler(new HealthEventHysteresis(1, 1)));

    private static PointHealthSnapshot Ready(params PointHealthItem[] items) => new([.. items], PointLastSeenIndexState.Ready);

    private static async Task<int> OpenFor(IHealthEventStore store, string subject) =>
        (await store.QueryAsync(new HealthEventQuery { SubjectId = subject, Lifecycle = HealthEventLifecycle.Open })).Total;

    [Fact]
    public async Task PointStale_OpensOneEvent_AndRecoveryClearsIt()
    {
        var (store, db) = await StoreAsync();
        await using var _ = db;
        var p = $"P-{Guid.NewGuid():N}";
        var gw = $"GW-{Guid.NewGuid():N}";
        var cycle = Cycle();

        await cycle.RunAsync(Ready(Item(p, FreshnessStatus.Stale, gw)), store, T, NullLogger.Instance, default);
        await cycle.RunAsync(Ready(Item(p, FreshnessStatus.Stale, gw)), store, T.AddMinutes(1), NullLogger.Instance, default);
        Assert.Equal(1, await OpenFor(store, p));

        await cycle.RunAsync(Ready(Item(p, FreshnessStatus.Fresh, gw)), store, T.AddMinutes(30), NullLogger.Instance, default);

        Assert.Equal(0, await OpenFor(store, p));
        var all = await store.QueryAsync(new HealthEventQuery { SubjectId = p });
        var e = Assert.Single(all.Items);
        Assert.Equal(T, e.RaisedAt);
        Assert.Equal(T.AddMinutes(30), e.ClearedAt);
    }

    [Fact]
    public async Task GatewayOutage_IsOneEvent_NotOnePerPoint()
    {
        var (store, db) = await StoreAsync();
        await using var _ = db;
        var gw = $"GW-{Guid.NewGuid():N}";
        var points = Enumerable.Range(0, 50).Select(i => $"{gw}-P{i}").ToArray();

        await Cycle().RunAsync(
            Ready([.. points.Select(p => Item(p, FreshnessStatus.Missing, gw, connected: false))]),
            store, T, NullLogger.Instance, default);

        Assert.Equal(1, await OpenFor(store, gw));
        foreach (var p in points.Take(5)) Assert.Equal(0, await OpenFor(store, p));
    }

    [Fact]
    public async Task TwoReplicas_ScanningTheSameLedger_LeaveOneOpenEvent()
    {
        var (a, dbA) = await StoreAsync();
        var (b, dbB) = await StoreAsync();
        await using var _a = dbA; await using var _b = dbB;
        var p = $"P-{Guid.NewGuid():N}";
        var snapshot = Ready(Item(p, FreshnessStatus.Stale, $"GW-{Guid.NewGuid():N}"));

        // Each replica has its own reconciler and its own connection, racing on the same row.
        await Task.WhenAll(
            Cycle().RunAsync(snapshot, a, T, NullLogger.Instance, default),
            Cycle().RunAsync(snapshot, b, T, NullLogger.Instance, default));

        Assert.Equal(1, await OpenFor(a, p));
    }

    [Fact]
    public async Task IndexNotReady_DoesNotClearARealOpenEvent()
    {
        var (store, db) = await StoreAsync();
        await using var _ = db;
        var p = $"P-{Guid.NewGuid():N}";
        var gw = $"GW-{Guid.NewGuid():N}";
        await Cycle().RunAsync(Ready(Item(p, FreshnessStatus.Stale, gw)), store, T, NullLogger.Instance, default);

        // The api server restarted: the index is warming, so every point reads Unknown.
        await Cycle().RunAsync(
            new PointHealthSnapshot([Item(p, FreshnessStatus.Unknown, gw)], PointLastSeenIndexState.Warming),
            store, T.AddMinutes(5), NullLogger.Instance, default);

        Assert.Equal(1, await OpenFor(store, p));
    }
}
