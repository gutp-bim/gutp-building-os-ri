using System.Text.Json;
using BuildingOS.Shared.Infrastructure.HealthEvents;

namespace BuildingOS.Shared.Domain.Health;

/// <summary>Identity of one event: at most one is open per key (#455).</summary>
public readonly record struct HealthEventKey(string SubjectType, string SubjectId, string Kind);

/// <summary>An open event whose severity should change (its raise time and acknowledgement are kept).</summary>
public sealed record HealthEventUpdate(HealthEventKey Key, string Severity, string DetailJson);

/// <summary>What one scan decided: events to open, to re-grade, and to close.</summary>
public sealed record HealthEventPlan(
    IReadOnlyList<HealthEventCandidate> Raise,
    IReadOnlyList<HealthEventUpdate> Update,
    IReadOnlyList<Guid> Clear)
{
    public static readonly HealthEventPlan Empty = new([], [], []);
}

/// <summary>
/// How many <b>consecutive</b> scans a condition must hold before it is raised, and must be absent before it is
/// cleared (ADR-0005 D3's deadband, expressed as a count of scans rather than a width of threshold).
/// </summary>
public sealed record HealthEventHysteresis(int RaiseAfterScans = 2, int ClearAfterScans = 3)
{
    public static readonly HealthEventHysteresis Default = new();
}

/// <summary>
/// Turns one classified ledger scan into the changes to the persisted events (#455, ADR-0005 Phase 2b). Pure
/// except for the per-condition scan counters it keeps between calls (in memory — a restart only delays a
/// raise/clear by a few scans).
///
/// <para><b>A condition is in one of three states per scan</b>: <i>holds</i> (it is true now), <i>gone</i> (it is
/// observably false) or <i>unknown</i> (the scan cannot tell — the data did not arrive, the gateway is down, there
/// was no value). Only <i>gone</i> counts toward clearing; <i>unknown</i> leaves an open event exactly as it is, so
/// losing the data a condition was judged on never reads as the condition having cleared.</para>
///
/// <para><b>Storm control</b>: when a gateway disconnects, every point behind it goes stale and then missing at
/// once. Those points get no per-point event; the gateway has one <c>gateway_offline</c> event. Events already
/// open for its points are held (not cleared) while it is down, and clear on the normal schedule after it returns.</para>
/// </summary>
public sealed class HealthEventReconciler(HealthEventHysteresis? hysteresis = null)
{
    private readonly HealthEventHysteresis _h = hysteresis ?? HealthEventHysteresis.Default;
    private readonly Dictionary<HealthEventKey, int> _holdingFor = new();
    private readonly Dictionary<HealthEventKey, int> _goneFor = new();

    private enum Verdict { Holds, Gone, Unknown }

    private sealed record Desired(string Severity, string DetailJson);

    public HealthEventPlan Reconcile(IReadOnlyList<PointHealthItem> items, IReadOnlyList<HealthEventEntry> open)
    {
        var byPoint = new Dictionary<string, PointHealthItem>(StringComparer.Ordinal);
        foreach (var item in items) byPoint[item.PointId] = item;

        // Gateways seen in the ledger, with their connection state (null = could not be read).
        var gateways = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var gatewayPoints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.Gateway?.Id is not { Length: > 0 } id) continue;
            gateways[id] = item.Gateway.Connected;
            gatewayPoints[id] = gatewayPoints.GetValueOrDefault(id) + 1;
        }

        // What is true now.
        var desired = new Dictionary<HealthEventKey, Desired>();
        foreach (var (id, connected) in gateways)
        {
            if (connected == false)
                desired[new(HealthEventSubjects.Gateway, id, HealthEventKinds.GatewayOffline)] =
                    new(HealthEventSeverities.Critical, Json(new { pointCount = gatewayPoints[id] }));
        }
        foreach (var item in items)
        {
            if (IsBehindOfflineGateway(item)) continue; // collapsed into the gateway's one event
            if (item.Freshness.Status == FreshnessStatus.Stale)
                desired[new(HealthEventSubjects.Point, item.PointId, HealthEventKinds.Stale)] = new(
                    HealthEventSeverities.Warn,
                    Json(new { ageSeconds = item.Freshness.AgeSeconds, thresholdSeconds = item.Freshness.ThresholdSeconds, lastSeen = item.Freshness.LastSeen }));
            else if (item.Freshness.Status == FreshnessStatus.Missing)
                desired[new(HealthEventSubjects.Point, item.PointId, HealthEventKinds.Missing)] = new(
                    HealthEventSeverities.Warn,
                    Json(new { reason = item.Freshness.Reason?.ToString(), thresholdSeconds = item.Freshness.ThresholdSeconds }));
            if (item.Alarm.Status is AlarmStatus.Warn or AlarmStatus.Critical)
                desired[new(HealthEventSubjects.Point, item.PointId, HealthEventKinds.Alarm)] = new(
                    item.Alarm.Status == AlarmStatus.Critical ? HealthEventSeverities.Critical : HealthEventSeverities.Warn,
                    Json(new { value = item.Alarm.Value, violated = item.Alarm.Violated?.ToString() }));
        }

        var openByKey = open.ToDictionary(e => new HealthEventKey(e.SubjectType, e.SubjectId, e.Kind));
        var raise = new List<HealthEventCandidate>();
        var update = new List<HealthEventUpdate>();
        var clear = new List<Guid>();

        // Raise: the condition must hold for RaiseAfterScans consecutive scans.
        foreach (var (key, want) in desired)
        {
            _goneFor.Remove(key);
            if (openByKey.TryGetValue(key, out var existing))
            {
                _holdingFor.Remove(key);
                if (!string.Equals(existing.Severity, want.Severity, StringComparison.Ordinal))
                    update.Add(new(key, want.Severity, want.DetailJson));
                continue;
            }
            var held = _holdingFor.GetValueOrDefault(key) + 1;
            if (held >= _h.RaiseAfterScans)
            {
                raise.Add(new(key.SubjectType, key.SubjectId, key.Kind, want.Severity, want.DetailJson));
                _holdingFor.Remove(key);
            }
            else _holdingFor[key] = held;
        }
        // A condition that did not hold this scan restarts its raise count.
        foreach (var key in _holdingFor.Keys.Where(k => !desired.ContainsKey(k)).ToArray()) _holdingFor.Remove(key);

        // Clear: an open event must be observably gone for ClearAfterScans consecutive scans.
        foreach (var (key, e) in openByKey)
        {
            if (desired.ContainsKey(key)) continue;
            var verdict = Judge(e, byPoint, gateways);
            if (verdict != Verdict.Gone) { _goneFor.Remove(key); continue; }
            var gone = _goneFor.GetValueOrDefault(key) + 1;
            if (gone >= _h.ClearAfterScans)
            {
                clear.Add(e.Id);
                _goneFor.Remove(key);
            }
            else _goneFor[key] = gone;
        }
        foreach (var key in _goneFor.Keys.Where(k => !openByKey.ContainsKey(k)).ToArray()) _goneFor.Remove(key);

        return new HealthEventPlan(raise, update, clear);
    }

    private static bool IsBehindOfflineGateway(PointHealthItem item) => item.Gateway is { Connected: false };

    /// <summary>For an open event that is not true now: is it gone, or can this scan not tell?</summary>
    private static Verdict Judge(
        HealthEventEntry e, Dictionary<string, PointHealthItem> byPoint, Dictionary<string, bool?> gateways)
    {
        if (e.SubjectType == HealthEventSubjects.Gateway)
        {
            if (!gateways.TryGetValue(e.SubjectId, out var connected)) return Verdict.Gone; // no point references it any more
            return connected switch { true => Verdict.Gone, false => Verdict.Holds, null => Verdict.Unknown };
        }

        if (!byPoint.TryGetValue(e.SubjectId, out var item)) return Verdict.Gone; // the point left the twin
        // Its gateway is down: nothing about the point can be judged until it is back.
        if (IsBehindOfflineGateway(item)) return Verdict.Unknown;

        switch (e.Kind)
        {
            case HealthEventKinds.Stale:
            case HealthEventKinds.Missing:
                // Stale → Missing (or back) replaces the event, so "not this status" is gone — unless the scan cannot tell.
                return item.Freshness.Status == FreshnessStatus.Unknown ? Verdict.Unknown : Verdict.Gone;
            case HealthEventKinds.Alarm:
                // Only Normal clears an alarm. Unknown (no value / no thresholds) and Suppressed (the data is
                // stale, so the value is not current) leave it as it is.
                return item.Alarm.Status == AlarmStatus.Normal ? Verdict.Gone : Verdict.Unknown;
            default:
                return Verdict.Unknown;
        }
    }

    private static string Json(object o) => JsonSerializer.Serialize(o);
}
