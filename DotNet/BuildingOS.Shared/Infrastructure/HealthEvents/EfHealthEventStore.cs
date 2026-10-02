using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.Health;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BuildingOS.Shared.Infrastructure.HealthEvents;

/// <summary>PostgreSQL / EF Core implementation of <see cref="IHealthEventStore"/> (#455).</summary>
public sealed class EfHealthEventStore(RelationalDbContext context) : IHealthEventStore
{
    private const string UniqueViolation = "23505";

    public async Task<IReadOnlyList<HealthEventEntry>> ListOpenAsync(CancellationToken ct = default) =>
        await context.HealthEvents.AsNoTracking()
            .Where(e => e.ClearedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<RaiseOutcome> RaiseAsync(HealthEventCandidate c, DateTime now, CancellationToken ct = default)
    {
        var entry = new HealthEventEntry
        {
            Id = Guid.NewGuid(),
            SubjectType = c.SubjectType,
            SubjectId = c.SubjectId,
            Kind = c.Kind,
            Severity = c.Severity,
            RaisedAt = now,
            Detail = c.DetailJson,
        };
        context.HealthEvents.Add(entry);
        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            return RaiseOutcome.Raised;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // The partial unique index says one is already open — equivalent to ON CONFLICT DO NOTHING.
            context.Entry(entry).State = EntityState.Detached;
            return RaiseOutcome.AlreadyOpen;
        }
    }

    public Task UpdateOpenAsync(
        string subjectType, string subjectId, string kind, string severity, string detailJson,
        CancellationToken ct = default) =>
        context.HealthEvents
            .Where(e => e.SubjectType == subjectType && e.SubjectId == subjectId && e.Kind == kind && e.ClearedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(e => e.Severity, severity).SetProperty(e => e.Detail, detailJson), ct);

    public Task<int> ClearAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken ct = default) =>
        ids.Count == 0
            ? Task.FromResult(0)
            : context.HealthEvents
                .Where(e => ids.Contains(e.Id) && e.ClearedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ClearedAt, now), ct);

    public async Task<HealthEventPage> QueryAsync(HealthEventQuery q, CancellationToken ct = default)
    {
        var rows = context.HealthEvents.AsNoTracking().AsQueryable();

        if (q.Lifecycle == HealthEventLifecycle.Open) rows = rows.Where(e => e.ClearedAt == null);
        else if (q.Lifecycle == HealthEventLifecycle.Cleared) rows = rows.Where(e => e.ClearedAt != null);

        if (q.Ack == HealthEventAckFilter.Acked) rows = rows.Where(e => e.AcknowledgedAt != null);
        else if (q.Ack == HealthEventAckFilter.Unacked) rows = rows.Where(e => e.AcknowledgedAt == null);

        if (q.Kinds.Count > 0)
        {
            var kinds = q.Kinds.ToArray();
            rows = rows.Where(e => kinds.Contains(e.Kind));
        }
        if (!string.IsNullOrEmpty(q.SubjectType)) rows = rows.Where(e => e.SubjectType == q.SubjectType);
        if (!string.IsNullOrEmpty(q.SubjectId)) rows = rows.Where(e => e.SubjectId == q.SubjectId);
        if (q.Since is { } since) rows = rows.Where(e => e.RaisedAt >= since);

        if (q.Scope is { } scope)
        {
            var points = scope.PointIds.ToArray();
            var gateways = scope.GatewayIds.ToArray();
            rows = rows.Where(e =>
                (e.SubjectType == HealthEventSubjects.Point && points.Contains(e.SubjectId))
                || (e.SubjectType == HealthEventSubjects.Gateway && gateways.Contains(e.SubjectId)));
        }

        var total = await rows.CountAsync(ct).ConfigureAwait(false);
        var items = await rows
            .OrderByDescending(e => e.RaisedAt).ThenByDescending(e => e.Id)
            .Skip(Math.Max(0, q.Offset)).Take(Math.Clamp(q.Limit, 1, 500))
            .ToListAsync(ct).ConfigureAwait(false);
        return new HealthEventPage(items, total);
    }

    public Task<HealthEventEntry?> GetAsync(Guid id, CancellationToken ct = default) =>
        context.HealthEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<HealthEventAckResult?> AcknowledgeAsync(
        Guid id, string actorSub, string? actorName, DateTime now, CancellationToken ct = default)
    {
        // Conditional on "not yet acknowledged", so two operators racing leave the first one on record
        // and the call stays idempotent.
        var applied = await context.HealthEvents
            .Where(e => e.Id == id && e.AcknowledgedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.AcknowledgedAt, now)
                .SetProperty(e => e.AcknowledgedBy, actorSub)
                .SetProperty(e => e.AcknowledgedByName, actorName), ct).ConfigureAwait(false) > 0;
        var current = await GetAsync(id, ct).ConfigureAwait(false);
        // `Applied` is the rows-affected of the conditional update — true for exactly one of any set of racing callers.
        return current is null ? null : new HealthEventAckResult(current, applied);
    }

    public Task<int> PruneClearedAsync(DateTime clearedBefore, CancellationToken ct = default) =>
        context.HealthEvents
            .Where(e => e.ClearedAt != null && e.ClearedAt < clearedBefore)
            .ExecuteDeleteAsync(ct);
}
