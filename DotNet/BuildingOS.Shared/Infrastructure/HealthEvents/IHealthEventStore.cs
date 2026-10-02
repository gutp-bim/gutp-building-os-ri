using BuildingOS.Shared.Domain.Health;

namespace BuildingOS.Shared.Infrastructure.HealthEvents;

/// <summary>A condition the evaluator wants to be open (#455).</summary>
public sealed record HealthEventCandidate(
    string SubjectType, string SubjectId, string Kind, string Severity, string DetailJson);

public enum RaiseOutcome
{
    /// <summary>A new event was opened.</summary>
    Raised,

    /// <summary>One was already open for that subject × kind (another replica, or an earlier scan) — nothing was added.</summary>
    AlreadyOpen,
}

public enum HealthEventLifecycle { Open, Cleared }

public enum HealthEventAckFilter { Acked, Unacked }

/// <summary>
/// The subjects a caller may see. <c>null</c> on the query means unrestricted (admin); a scope with an empty
/// list for a subject type means none of that type. Built from the caller's readable points, so an event is
/// never listed for something the caller cannot read.
/// </summary>
public sealed record HealthEventScope(IReadOnlyCollection<string> PointIds, IReadOnlyCollection<string> GatewayIds);

/// <summary>
/// Filters for listing events. <b>Lifecycle and acknowledgement are separate</b> (they are independent axes,
/// #455) — there is deliberately no combined open/acked/cleared state.
/// </summary>
public sealed class HealthEventQuery
{
    public HealthEventLifecycle? Lifecycle { get; set; }
    public HealthEventAckFilter? Ack { get; set; }
    public IReadOnlyList<string> Kinds { get; set; } = [];
    public string? SubjectType { get; set; }
    public string? SubjectId { get; set; }

    /// <summary>Only events raised at or after this instant (UTC).</summary>
    public DateTime? Since { get; set; }

    public HealthEventScope? Scope { get; set; }
    public int Limit { get; set; } = 100;
    public int Offset { get; set; }
}

public sealed record HealthEventPage(IReadOnlyList<HealthEventEntry> Items, int Total);

/// <summary>
/// The event after an acknowledgement attempt, and whether <b>this call</b> is the one that acknowledged it
/// (false: someone else already had, and their acknowledgement is what <see cref="Event"/> shows).
/// </summary>
public sealed record HealthEventAckResult(HealthEventEntry Event, bool Applied);

/// <summary>
/// Persistence of health events (#455). Every write is idempotent, so N evaluator replicas (or a retried
/// scan) converge on the same rows instead of needing a lock.
/// </summary>
public interface IHealthEventStore
{
    Task<IReadOnlyList<HealthEventEntry>> ListOpenAsync(CancellationToken ct = default);

    /// <summary>Opens the event unless one is already open for the same subject × kind.</summary>
    Task<RaiseOutcome> RaiseAsync(HealthEventCandidate candidate, DateTime now, CancellationToken ct = default);

    /// <summary>Refreshes the severity / detail snapshot of the open event (its raise time and ack are kept).</summary>
    Task UpdateOpenAsync(
        string subjectType, string subjectId, string kind, string severity, string detailJson,
        CancellationToken ct = default);

    /// <summary>Closes the given events. Already-cleared ones are untouched. Returns how many were closed.</summary>
    Task<int> ClearAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken ct = default);

    Task<HealthEventPage> QueryAsync(HealthEventQuery query, CancellationToken ct = default);

    Task<HealthEventEntry?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Acknowledges the event, once: a second acknowledgement returns the existing one unchanged (the first
    /// acknowledger stays on record) with <c>Applied = false</c>. Null when no such event exists.
    /// </summary>
    Task<HealthEventAckResult?> AcknowledgeAsync(
        Guid id, string actorSub, string? actorName, DateTime now, CancellationToken ct = default);

    /// <summary>Deletes cleared events cleared before <paramref name="clearedBefore"/>. Open events are never deleted.</summary>
    Task<int> PruneClearedAsync(DateTime clearedBefore, CancellationToken ct = default);
}
