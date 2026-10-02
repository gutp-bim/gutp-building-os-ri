using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Infrastructure.HealthEvents;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #455 against real PostgreSQL: the "open once per subject × kind" guarantee (a partial unique index, with
/// no NULLs in its key — the case a nullable point_id/gateway_id pair would have got wrong), the independent
/// lifecycle / acknowledgement axes, scope filtering and retention.
/// </summary>
[Collection(Names.Postgres)]
public class HealthEventStoreTest(PostgresFixture postgres) : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private async Task<(IHealthEventStore Store, RelationalDbContext Db)> NewStoreAsync()
    {
        var db = new RelationalDbContext(new DbContextOptionsBuilder<RelationalDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();
        return (new EfHealthEventStore(db), db);
    }

    // Unique subject ids per test: the container is shared across the collection.
    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static HealthEventCandidate Cand(string type, string subject, string kind, string severity = "warn") =>
        new(type, subject, kind, severity, "{}");

    [Fact]
    public async Task Raise_TwiceForTheSameGateway_LeavesOneOpenEvent()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var gw = Id("GW");

        var first = await store.RaiseAsync(Cand("gateway", gw, "gateway_offline", "critical"), T0);
        var second = await store.RaiseAsync(Cand("gateway", gw, "gateway_offline", "critical"), T0.AddMinutes(1));

        Assert.Equal(RaiseOutcome.Raised, first);
        Assert.Equal(RaiseOutcome.AlreadyOpen, second);
        var page = await store.QueryAsync(new HealthEventQuery { SubjectType = "gateway", SubjectId = gw });
        Assert.Equal(1, page.Total);
        Assert.Equal(T0, page.Items[0].RaisedAt); // the first raise time is the one kept
    }

    [Fact]
    public async Task Raise_ConcurrentlyFromTwoReplicas_StillLeavesOneOpenEvent()
    {
        var (a, dbA) = await NewStoreAsync();
        var (b, dbB) = await NewStoreAsync();
        await using var _a = dbA; await using var _b = dbB;
        var point = Id("P");

        var outcomes = await Task.WhenAll(
            a.RaiseAsync(Cand("point", point, "stale"), T0),
            b.RaiseAsync(Cand("point", point, "stale"), T0));

        Assert.Single(outcomes, RaiseOutcome.Raised);
        Assert.Equal(1, (await a.QueryAsync(new HealthEventQuery { SubjectId = point })).Total);
    }

    [Fact]
    public async Task DifferentKindsOrSubjects_AreIndependentlyOpen()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var p = Id("P");

        Assert.Equal(RaiseOutcome.Raised, await store.RaiseAsync(Cand("point", p, "stale"), T0));
        Assert.Equal(RaiseOutcome.Raised, await store.RaiseAsync(Cand("point", p, "alarm"), T0));
        Assert.Equal(RaiseOutcome.Raised, await store.RaiseAsync(Cand("point", Id("P"), "stale"), T0));
    }

    [Fact]
    public async Task AfterClear_TheSameConditionCanBeRaisedAgain_AsANewEvent()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var p = Id("P");
        await store.RaiseAsync(Cand("point", p, "missing"), T0);
        var open = (await store.ListOpenAsync()).Single(e => e.SubjectId == p);

        Assert.Equal(1, await store.ClearAsync([open.Id], T0.AddMinutes(30)));
        var again = await store.RaiseAsync(Cand("point", p, "missing"), T0.AddHours(1));

        Assert.Equal(RaiseOutcome.Raised, again);
        var all = await store.QueryAsync(new HealthEventQuery { SubjectId = p });
        Assert.Equal(2, all.Total);
        Assert.Single(all.Items, e => e.IsOpen);
    }

    [Fact]
    public async Task Clear_IsIdempotent_AndKeepsTheFirstClearTime()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var p = Id("P");
        await store.RaiseAsync(Cand("point", p, "stale"), T0);
        var id = (await store.ListOpenAsync()).Single(e => e.SubjectId == p).Id;

        Assert.Equal(1, await store.ClearAsync([id], T0.AddMinutes(10)));
        Assert.Equal(0, await store.ClearAsync([id], T0.AddMinutes(20)));
        Assert.Equal(T0.AddMinutes(10), (await store.GetAsync(id))!.ClearedAt);
    }

    [Fact]
    public async Task UpdateOpen_ChangesSeverityAndDetail_ButNotRaiseTimeOrAck()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var p = Id("P");
        await store.RaiseAsync(Cand("point", p, "alarm", "warn"), T0);
        var id = (await store.ListOpenAsync()).Single(e => e.SubjectId == p).Id;
        await store.AcknowledgeAsync(id, "sub-1", "Yamada", T0.AddMinutes(1));

        await store.UpdateOpenAsync("point", p, "alarm", "critical", """{"value":99}""");

        var e = (await store.GetAsync(id))!;
        Assert.Equal("critical", e.Severity);
        Assert.Contains("99", e.Detail);
        Assert.Equal(T0, e.RaisedAt);
        Assert.Equal("sub-1", e.AcknowledgedBy);
    }

    [Fact]
    public async Task Acknowledge_RecordsWhoAndWhen_IsIdempotent_AndKeepsTheFirstAcknowledger()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var p = Id("P");
        await store.RaiseAsync(Cand("point", p, "stale"), T0);
        var id = (await store.ListOpenAsync()).Single(e => e.SubjectId == p).Id;

        var first = await store.AcknowledgeAsync(id, "sub-1", "Yamada", T0.AddMinutes(5));
        var second = await store.AcknowledgeAsync(id, "sub-2", "Suzuki", T0.AddMinutes(9));

        Assert.True(first!.Applied);
        Assert.Equal("sub-1", first.Event.AcknowledgedBy);
        Assert.False(second!.Applied);                       // the repeat did not acknowledge anything
        Assert.Equal("Yamada", second.Event.AcknowledgedByName);
        Assert.Equal(T0.AddMinutes(5), second.Event.AcknowledgedAt);
        Assert.Null(await store.AcknowledgeAsync(Guid.NewGuid(), "sub-1", null, T0));
    }

    [Fact]
    public async Task LifecycleAndAck_AreIndependentAxes_AllFourCombinationsExist()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var tag = Id("axes");
        string[] subjects = [$"{tag}-a", $"{tag}-b", $"{tag}-c", $"{tag}-d"];
        foreach (var s in subjects) await store.RaiseAsync(Cand("point", s, "stale"), T0);
        var ids = (await store.ListOpenAsync()).Where(e => e.SubjectId.StartsWith(tag)).ToDictionary(e => e.SubjectId, e => e.Id);
        // a: open+unacked   b: open+acked   c: cleared+unacked   d: cleared+acked (acked after it cleared)
        await store.AcknowledgeAsync(ids[subjects[1]], "u", null, T0);
        await store.ClearAsync([ids[subjects[2]], ids[subjects[3]]], T0.AddMinutes(1));
        await store.AcknowledgeAsync(ids[subjects[3]], "u", null, T0.AddMinutes(2));

        async Task<string[]> Q(HealthEventLifecycle l, HealthEventAckFilter a) =>
            (await store.QueryAsync(new HealthEventQuery { Lifecycle = l, Ack = a, Limit = 500 })).Items
                .Where(e => e.SubjectId.StartsWith(tag)).Select(e => e.SubjectId).ToArray();

        Assert.Equal([subjects[0]], await Q(HealthEventLifecycle.Open, HealthEventAckFilter.Unacked));
        Assert.Equal([subjects[1]], await Q(HealthEventLifecycle.Open, HealthEventAckFilter.Acked));
        Assert.Equal([subjects[2]], await Q(HealthEventLifecycle.Cleared, HealthEventAckFilter.Unacked));
        Assert.Equal([subjects[3]], await Q(HealthEventLifecycle.Cleared, HealthEventAckFilter.Acked));
    }

    [Fact]
    public async Task Query_Scope_ListsOnlyTheSubjectsTheCallerMayRead()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var tag = Id("scope");
        string mine = $"{tag}-mine", other = $"{tag}-other", gwMine = $"{tag}-gw", gwOther = $"{tag}-gw2";
        await store.RaiseAsync(Cand("point", mine, "stale"), T0);
        await store.RaiseAsync(Cand("point", other, "stale"), T0);
        await store.RaiseAsync(Cand("gateway", gwMine, "gateway_offline", "critical"), T0);
        await store.RaiseAsync(Cand("gateway", gwOther, "gateway_offline", "critical"), T0);

        var page = await store.QueryAsync(new HealthEventQuery
        {
            Scope = new HealthEventScope([mine], [gwMine]),
            Limit = 500,
        });

        Assert.Equal(new[] { mine, gwMine }.Order(), page.Items.Select(e => e.SubjectId).Order());
        Assert.Empty((await store.QueryAsync(new HealthEventQuery { Scope = new HealthEventScope([], []) })).Items);
    }

    [Fact]
    public async Task Prune_RemovesOnlyOldClearedEvents_NeverOpenOnes()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var tag = Id("prune");
        string oldCleared = $"{tag}-old", recentCleared = $"{tag}-recent", open = $"{tag}-open";
        foreach (var s in new[] { oldCleared, recentCleared, open }) await store.RaiseAsync(Cand("point", s, "stale"), T0.AddDays(-200));
        var ids = (await store.ListOpenAsync()).Where(e => e.SubjectId.StartsWith(tag)).ToDictionary(e => e.SubjectId, e => e.Id);
        await store.ClearAsync([ids[oldCleared]], T0.AddDays(-100));
        await store.ClearAsync([ids[recentCleared]], T0.AddDays(-1));

        var removed = await store.PruneClearedAsync(T0.AddDays(-90));

        Assert.True(removed >= 1);
        Assert.Null(await store.GetAsync(ids[oldCleared]));
        Assert.NotNull(await store.GetAsync(ids[recentCleared]));
        Assert.NotNull(await store.GetAsync(ids[open]));
    }

    [Fact]
    public async Task Query_Pages_NewestFirst()
    {
        var (store, db) = await NewStoreAsync();
        await using var _ = db;
        var tag = Id("page");
        for (var i = 0; i < 5; i++) await store.RaiseAsync(Cand("point", $"{tag}-{i}", "stale"), T0.AddMinutes(i));

        var page = await store.QueryAsync(new HealthEventQuery { SubjectType = "point", Since = T0, Limit = 2, Offset = 0 });

        Assert.True(page.Total >= 5);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Items[0].RaisedAt >= page.Items[1].RaisedAt);
    }

    [Fact]
    public async Task Acknowledge_RacingCallers_ExactlyOneIsApplied()
    {
        var (a, dbA) = await NewStoreAsync();
        var (b, dbB) = await NewStoreAsync();
        await using var _a = dbA; await using var _b = dbB;
        var p = Id("P");
        await a.RaiseAsync(Cand("point", p, "stale"), T0);
        var id = (await a.ListOpenAsync()).Single(e => e.SubjectId == p).Id;

        var results = await Task.WhenAll(
            a.AcknowledgeAsync(id, "same-user", "S", T0.AddMinutes(1)),
            b.AcknowledgeAsync(id, "same-user", "S", T0.AddMinutes(1)));

        Assert.Single(results, r => r!.Applied);
    }
}
