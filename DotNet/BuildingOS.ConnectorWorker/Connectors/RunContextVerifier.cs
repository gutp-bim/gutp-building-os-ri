using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BuildingOS.ConnectorWorker.Protos;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Opt-in verification of pre-distributed signed leases. No payload/signature is
/// read from telemetry. Receiver activation evidence is trusted only after the
/// existing mTLS Gateway identity binding. Nexus owns durable generation history;
/// RI retains original historical frames instead of reclassifying them by now.
/// </summary>
public sealed class RunContextVerifier
{
    private sealed record Trust(string GatewayId, string ClockId, byte[] PublicKey, string EnvelopeFile);
    private sealed record Envelope(byte[] Payload, byte[] Signature);
    private sealed record Lease(string Gateway, string Clock, string Revision, string Run, string Generation,
        string Status, decimal Issued, decimal From, decimal Expires);
    private readonly IReadOnlyList<Lease> leases;
    private RunContextVerifier(IReadOnlyList<Lease> leases) => this.leases = leases;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Regex InstantPattern = new(
        @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?Z$", RegexOptions.CultureInvariant);
    private static readonly Regex GenerationPattern = new(@"^[1-9][0-9]{0,15}$", RegexOptions.CultureInvariant);

    public static RunContextVerifier Load(string path)
    {
        var entries = JsonSerializer.Deserialize<Trust[]>(ReadBounded(path, 2097152), JsonOptions)
            ?? throw new InvalidOperationException("Run context trust configuration is empty");
        if (entries.Length == 0) throw new InvalidOperationException("Run context trust configuration is empty");
        var result = new List<Lease>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.GatewayId) || string.IsNullOrWhiteSpace(entry.ClockId)
                || entry.PublicKey?.Length != 32 || !Path.IsPathFullyQualified(entry.EnvelopeFile))
                throw new InvalidOperationException("Invalid run context trust scope/key/path");
            var envelope = JsonSerializer.Deserialize<Envelope>(ReadBounded(entry.EnvelopeFile, 100000), JsonOptions)
                ?? throw new InvalidOperationException("Missing run context envelope");
            if (envelope.Payload is null || envelope.Payload.Length > 65536 || envelope.Signature?.Length != 64)
                throw new InvalidOperationException("Invalid run context envelope size");
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(entry.PublicKey, 0));
            signer.BlockUpdate(envelope.Payload, 0, envelope.Payload.Length);
            if (!signer.VerifySignature(envelope.Signature)) throw new InvalidOperationException("Invalid run context signature");
            using var document = JsonDocument.Parse(envelope.Payload);
            var root = document.RootElement;
            var properties = root.EnumerateObject().Select(p => p.Name).ToArray();
            string[] required = ["schemaVersion", "gatewayId", "clockId", "provisioningRevision", "runId", "generation", "status", "issuedAt", "validFrom", "expiresAt"];
            if (properties.Length != required.Length || !properties.Order().SequenceEqual(required.Order()))
                throw new InvalidOperationException("Invalid run context schema");
            string Text(string name) => root.GetProperty(name).GetString() ?? throw new InvalidOperationException("Missing run context field");
            var generation = root.GetProperty("generation").GetRawText();
            if (!GenerationPattern.IsMatch(generation) || !ulong.TryParse(generation, out var number) || number > 9007199254740991)
                throw new InvalidOperationException("Invalid run context generation");
            var lease = new Lease(Text("gatewayId"), Text("clockId"), Text("provisioningRevision"), Text("runId"), generation,
                Text("status"), Instant(Text("issuedAt")), Instant(Text("validFrom")), Instant(Text("expiresAt")));
            if (Text("schemaVersion") != "clock-run-context/2-draft" || lease.Gateway != entry.GatewayId || lease.Clock != entry.ClockId
                || string.IsNullOrWhiteSpace(lease.Run) || string.IsNullOrWhiteSpace(lease.Revision)
                || lease.Status is not ("active" or "revoked") || lease.Issued > lease.From || lease.From >= lease.Expires)
                throw new InvalidOperationException("Invalid signed run context contract");
            if (result.Any(l => l.Gateway == lease.Gateway && l.Clock == lease.Clock && l.Generation == lease.Generation))
                throw new InvalidOperationException("Duplicate run context generation");
            result.Add(lease);
        }
        return new RunContextVerifier(result);
    }

    public bool Validate(TelemetryFrame frame, string? trustedGatewayId, out string reason)
    {
        reason = "run_context_invalid";
        var attrs = frame.Attributes;
        var status = attrs.GetValueOrDefault("sim_run_status");
        if (status is null or "absent")
            return !attrs.ContainsKey("sim_run_id") && !attrs.ContainsKey("sim_context_generation");
        if (status is not ("valid" or "invalid" or "unverified") || trustedGatewayId != frame.GatewayId)
            return false;
        var candidates = leases.Where(l => l.Gateway == frame.GatewayId && l.Clock == attrs.GetValueOrDefault("sim_clock_id")
            && l.Revision == attrs.GetValueOrDefault("sim_config_revision")).ToArray();
        if (candidates.Length == 0) return false;
        try
        {
            var start = Instant(attrs["sim_poll_started_at"]);
            var end = Instant(attrs["sim_poll_ended_at"]);
            var observed = Instant(frame.Timestamp);
            if (end < start || observed < start || observed > end) return false;
            if (status != "valid")
                return !attrs.ContainsKey("sim_run_id") && !attrs.ContainsKey("sim_context_generation")
                    && !string.IsNullOrWhiteSpace(attrs.GetValueOrDefault("sim_run_reason"));
            var lease = candidates.SingleOrDefault(l => l.Run == attrs.GetValueOrDefault("sim_run_id")
                && l.Generation == attrs.GetValueOrDefault("sim_context_generation"));
            if (lease is null || lease.Status != "active" || attrs.ContainsKey("sim_run_reason")) return false;
            var commit = Instant(attrs["sim_context_committed_at"]);
            var apply = Instant(attrs["sim_context_applied_at"]);
            if (lease.Issued > commit || commit > apply || apply > start || start < lease.From || end >= lease.Expires) return false;
            var quality = attrs.GetValueOrDefault("sim_time_quality");
            if (quality == "stable_unfenced")
                return double.TryParse(attrs.GetValueOrDefault("sim_time_s"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    && double.IsFinite(seconds) && seconds >= 0;
            return quality is "changed" or "missing" && !attrs.ContainsKey("sim_time_s");
        }
        catch (Exception exception) when (exception is FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    // Compare nanoseconds exactly: rounding to .NET ticks could incorrectly
    // accept a poll starting 1 ns before apply, or ending at lease expiry.
    private static decimal Instant(string text)
    {
        var match = InstantPattern.Match(text);
        if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd'T'HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            throw new FormatException("Invalid UTC acquisition time");
        var fraction = match.Groups[2].Value.PadRight(9, '0');
        return (decimal)at.ToUnixTimeSeconds() * 1000000000 + int.Parse(fraction, CultureInfo.InvariantCulture);
    }
    private static byte[] ReadBounded(string path, int maxBytes)
    {
        using var file = File.OpenRead(path);
        if (file.Length > maxBytes) throw new InvalidOperationException("Run context input is too large");
        using var buffer = new MemoryStream(); file.CopyTo(buffer);
        if (buffer.Length > maxBytes) throw new InvalidOperationException("Run context input changed size");
        return buffer.ToArray();
    }
}
