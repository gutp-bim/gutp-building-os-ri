namespace BuildingOS.Shared.Infrastructure.Messaging;

/// <summary>
/// Canonical JetStream topology for the <c>building-os.*</c> subject space.
///
/// Every worker that shares a stream must resolve the same stream name and the
/// same full subject set, so that whichever worker starts first creates the
/// stream with all of its subjects (not just its own). This keeps the runtime
/// aligned with docs/architecture/oss-nats-design.md and removes the previous
/// startup-order-dependent data-loss behaviour.
/// </summary>
public static class NatsStreamTopology
{
    private static readonly (string Prefix, string StreamName, string[] Subjects)[] KnownStreams =
    [
        ("building-os.raw.",       "BUILDING_OS_RAW",       ["building-os.raw.>"]),
        ("building-os.validated.", "BUILDING_OS_VALIDATED", ["building-os.validated.>"]),
        // Deliberately the literal durable subject, NOT `building-os.control.>` (#381).
        // Only the generic request subject is consumed from JetStream (the `pointcontrolworker`
        // durable, whose FilterSubject is this exact string). The other two subjects under
        // `building-os.control.` are core-NATS request/reply and must stay out of the stream:
        //
        //   building-os.control.request.gw.{gatewayId}  per-gateway egress (GatewayBridge subscribes
        //     with core `nats.SubscribeAsync`, not JetStream). The publisher sends this as a NATS
        //     *request* and uses `NatsNoRespondersException` to detect an offline gateway and fail
        //     fast with 503 (#186). A stream bound to `building-os.control.>` registers interest on
        //     this subject and **replies with a PubAck**, so no-responders can never fire and the
        //     503 branch is unreachable — the caller instead waits out the result timeout while the
        //     audit row stays `pending`. Verified with `nats request`: the wildcard binding answers
        //     `{"stream":"BUILDING_OS_CONTROL","seq":N}` in <1 ms, an unbound subject answers
        //     "No responders are available".
        //   building-os.control.result.{controlId}      consumed by WaitForResult over core NATS.
        //
        // Note that `building-os.control.request` does not match `building-os.control.request.gw.X`:
        // NATS literal subjects match token-for-token, so the per-gateway subject is a distinct
        // subject rather than a child of this one.
        ("building-os.control.",   "BUILDING_OS_CONTROL",   ["building-os.control.request"]),
        ("building-os.dlq.",       "BUILDING_OS_DLQ",       ["building-os.dlq.>"]),
    ];

    /// <summary>
    /// Resolves a subscription subject to its owning JetStream stream and the
    /// complete subject set that stream must capture.
    /// </summary>
    public static (string StreamName, string[] StreamSubjects) Resolve(string subject)
    {
        foreach (var (prefix, stream, subjects) in KnownStreams)
        {
            if (subject.StartsWith(prefix, StringComparison.Ordinal))
                return (stream, subjects.ToArray());
        }

        // Fallback: preserve prior single-stream behaviour for any unmapped subject.
        var streamPrefix = subject.Split('.').First().ToUpperInvariant().Replace('-', '_');
        return (streamPrefix, new[] { subject, $"{subject}.>" });
    }

    /// <summary>
    /// Same as <see cref="Resolve"/> but throws <see cref="InvalidOperationException"/>
    /// if <paramref name="subject"/> does not belong to a known <c>building-os.*</c> stream.
    /// Call this at worker startup to fail fast on misconfigured subjects.
    /// </summary>
    public static (string StreamName, string[] StreamSubjects) ResolveOrThrow(string subject)
    {
        foreach (var (prefix, stream, subjects) in KnownStreams)
        {
            if (subject.StartsWith(prefix, StringComparison.Ordinal))
                return (stream, subjects.ToArray());
        }

        var known = string.Join(", ", KnownStreams.Select(k => $"building-os.{k.Prefix.Split('.')[1]}.*"));
        throw new InvalidOperationException(
            $"Subject '{subject}' is not in a known stream. Known prefixes: {known}");
    }
}
