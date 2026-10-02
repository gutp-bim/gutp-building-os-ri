namespace BuildingOS.Shared.Domain.Health;

/// <summary>What a health event is about (#455). Extensible to device / building.</summary>
public static class HealthEventSubjects
{
    public const string Point = "point";
    public const string Gateway = "gateway";
    public static readonly IReadOnlyList<string> All = [Point, Gateway];
}

/// <summary>The condition a health event records (#455).</summary>
public static class HealthEventKinds
{
    public const string Stale = "stale";
    public const string Missing = "missing";
    public const string Alarm = "alarm";
    public const string GatewayOffline = "gateway_offline";
    public static readonly IReadOnlyList<string> All = [Stale, Missing, Alarm, GatewayOffline];
}

public static class HealthEventSeverities
{
    public const string Warn = "warn";
    public const string Critical = "critical";
}

/// <summary>
/// One persisted health event (#455, ADR-0005 Phase 2b): a condition on a subject that was raised and —
/// later — cleared. <b>Lifecycle</b> (<see cref="ClearedAt"/>: open / cleared) and <b>acknowledgement</b>
/// (<see cref="AcknowledgedAt"/>) are independent axes: all four combinations exist, and a cleared event
/// can still be acknowledged ("I saw it").
///
/// <para>At most one event is open per (subject, kind) — enforced by a partial unique index, so a second
/// evaluator replica raising the same condition converges on the first one's row instead of duplicating it.</para>
/// </summary>
public sealed class HealthEventEntry
{
    public Guid Id { get; set; }

    /// <see cref="HealthEventSubjects"/>
    public string SubjectType { get; set; } = string.Empty;

    /// <summary>The business id: a pointId, or a gatewayId.</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <see cref="HealthEventKinds"/>
    public string Kind { get; set; } = string.Empty;

    /// <see cref="HealthEventSeverities"/>
    public string Severity { get; set; } = string.Empty;

    public DateTime RaisedAt { get; set; }

    /// <summary>Null while the condition holds (open).</summary>
    public DateTime? ClearedAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>Keycloak <c>sub</c> of whoever acknowledged — the stable identity.</summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>Display name at the time of acknowledgement (the sub alone reads as noise).</summary>
    public string? AcknowledgedByName { get; set; }

    /// <summary>jsonb snapshot of why it was raised: the threshold, the value, the reason, the age…</summary>
    public string Detail { get; set; } = "{}";

    public bool IsOpen => ClearedAt is null;
}
