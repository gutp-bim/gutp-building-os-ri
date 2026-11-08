using System.Text.Json.Serialization;

namespace BuildingOS.Shared.Domain.Health;

/// <summary>Why a point is left out of data health (#635).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotMonitoredReason
{
    /// <summary>A release point (<c>bos:releaseOf</c>, #600): a control target, never a value source.</summary>
    ReleasePoint,

    /// <summary>The twin declares <c>bos:reportsTelemetry false</c>.</summary>
    DeclaredNoTelemetry,
}

/// <summary>Whether data health judges this point, and if not, why. <c>Reason</c> is null exactly when monitored.</summary>
public sealed record PointMonitoring(bool Monitored, NotMonitoredReason? Reason)
{
    public static readonly PointMonitoring Yes = new(true, null);
}

/// <summary>
/// Decides which points data health (freshness / alarm, and the health events raised from them) judges at all
/// (#635). Some points structurally never report a value; classifying them reads as "missing" forever and the
/// evaluator would keep an event open on each of them.
///
/// <para>Deliberately <b>not</b> <c>writable</c>: a BACnet AO/BO is writable and reports its present value, and
/// dropping it would hide a real outage. Only what the twin says about telemetry counts — a release point is
/// never a value source, and anything else must be declared.</para>
/// </summary>
public static class PointMonitoringPolicy
{
    public static PointMonitoring Resolve(bool isReleasePoint, bool? reportsTelemetry)
    {
        if (isReleasePoint) return new(false, NotMonitoredReason.ReleasePoint);
        if (reportsTelemetry == false) return new(false, NotMonitoredReason.DeclaredNoTelemetry);
        return PointMonitoring.Yes;
    }

    public static PointMonitoring Resolve(Point point) => Resolve(point.IsReleasePoint, point.ReportsTelemetry);
}
