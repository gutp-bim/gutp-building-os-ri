using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.PointControl;
using BuildingOS.Shared.Infrastructure.PointControlAudit;
using BuildingOS.Shared.Infrastructure.PointControlRepository;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>
/// #478: time-range + keyset paging of <c>point_control_audit</c> against real PostgreSQL. The cursor
/// compares (created_at, id) in the database, so ties on created_at and rows inserted between pages
/// must neither be skipped nor repeated.
/// </summary>
[Collection(Names.Postgres)]
public class ControlAuditPagingTest(PostgresFixture postgres) : IntegrationTestBase
{
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Paging_VisitsEveryRowOnce_NewestFirst_IncludingCreatedAtTies()
    {
        var pointId = $"PT-page-{Guid.NewGuid():N}";
        await using var db = await NewContextAsync();
        // 7 rows; three share one timestamp so the id tie-breaker decides their order.
        var times = new[] { 0, 1, 1, 1, 2, 3, 4 }.Select(m => T0.AddMinutes(m)).ToArray();
        var seeded = await SeedAsync(db, pointId, times);

        var repo = new EfPointControlRepository(db);
        var visited = new List<Guid>();
        ControlAuditCursor? after = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await repo.ListAuditByPointAsync(new ControlAuditQuery(pointId, 3, After: after), default);
            visited.AddRange(page.Select(e => e.Id));
            if (page.Count < 3) break;
            after = new ControlAuditCursor(page[^1].CreatedAt, page[^1].Id);
        }

        Assert.Equal(seeded.Count, visited.Count);
        Assert.Equal(seeded.Select(e => e.Id).ToHashSet(), visited.ToHashSet());
        var createdAt = seeded.ToDictionary(e => e.Id, e => e.CreatedAt);
        Assert.Equal(visited.Select(id => createdAt[id]).OrderByDescending(t => t), visited.Select(id => createdAt[id]));
    }

    [Fact]
    public async Task Paging_IsNotShiftedByRowsInsertedBetweenPages()
    {
        var pointId = $"PT-page-{Guid.NewGuid():N}";
        await using var db = await NewContextAsync();
        await SeedAsync(db, pointId, Enumerable.Range(0, 4).Select(m => T0.AddMinutes(m)).ToArray());
        var repo = new EfPointControlRepository(db);

        var first = await repo.ListAuditByPointAsync(new ControlAuditQuery(pointId, 2), default);
        await SeedAsync(db, pointId, new[] { T0.AddMinutes(10) }); // a new write lands mid-backfill
        var second = await repo.ListAuditByPointAsync(
            new ControlAuditQuery(pointId, 2, After: new ControlAuditCursor(first[^1].CreatedAt, first[^1].Id)), default);

        Assert.Equal(new[] { T0.AddMinutes(1), T0 }, second.Select(e => e.CreatedAt));
    }

    [Fact]
    public async Task Range_IsStartInclusive_EndExclusive()
    {
        var pointId = $"PT-page-{Guid.NewGuid():N}";
        await using var db = await NewContextAsync();
        await SeedAsync(db, pointId, Enumerable.Range(0, 5).Select(m => T0.AddMinutes(m)).ToArray());
        var repo = new EfPointControlRepository(db);

        var rows = await repo.ListAuditByPointAsync(
            new ControlAuditQuery(pointId, 10, Start: T0.AddMinutes(1), End: T0.AddMinutes(3)), default);

        Assert.Equal(new[] { T0.AddMinutes(2), T0.AddMinutes(1) }, rows.Select(e => e.CreatedAt));
    }

    private async Task<RelationalDbContext> NewContextAsync()
    {
        var db = new RelationalDbContext(new DbContextOptionsBuilder<RelationalDbContext>()
            .UseNpgsql(postgres.ConnectionString).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    private static async Task<List<PointControlAuditEntry>> SeedAsync(
        RelationalDbContext db, string pointId, IReadOnlyList<DateTime> createdAt)
    {
        var rows = createdAt.Select(t => new PointControlAuditEntry
        {
            Id = Guid.NewGuid(), PointId = pointId, Request = "{}", CreatedAt = t, ActorSub = "it",
        }).ToList();
        db.PointControlAudits.AddRange(rows);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return rows;
    }
}
