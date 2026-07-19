using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// NATS KV-backed gateway egress connection heartbeat (#230 Phase 2②, ADR-0004). Bucket
/// "gateway-connection", history=1, with a bucket-level <see cref="NatsKVConfig.MaxAge"/> TTL so an
/// entry a crashed replica never got to delete still expires → <c>connected=false</c>. Mirrors the
/// <see cref="NatsKvLatestStore"/> wiring (same <see cref="INatsJSContext"/> injection + lazy store).
///
/// All operations are <b>best-effort</b>: every method swallows and logs exceptions and never throws,
/// so a KV problem can never break the egress stream or the admin read path.
/// </summary>
public sealed class NatsKvGatewayConnectionStore : IGatewayConnectionStatusStore
{
    /// <summary>Default TTL (seconds). Reader and writer must agree on the bucket config, so both default here.</summary>
    public const int DefaultTtlSeconds = 30;

    private const string BucketName = "gateway-connection";
    private static readonly Regex _invalidKey = new(@"[^a-zA-Z0-9_.\-]", RegexOptions.Compiled);
    // Control chars (incl. CR/LF) stripped from a gateway_id before it reaches a log sink, so a crafted
    // id can't forge log lines (CodeQL "log entries created from user input").
    private static readonly Regex _controlChars = new(@"\p{C}", RegexOptions.Compiled);

    private readonly INatsJSContext _js;
    private readonly ILogger<NatsKvGatewayConnectionStore> _logger;
    private readonly TimeSpan _ttl;
    private INatsKVStore? _kv;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public NatsKvGatewayConnectionStore(
        INatsJSContext js,
        ILogger<NatsKvGatewayConnectionStore> logger,
        TimeSpan? ttl = null)
    {
        _js = js;
        _logger = logger;
        _ttl = ttl is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(DefaultTtlSeconds);
    }

    private async Task<INatsKVStore> GetKvAsync(CancellationToken ct)
    {
        if (_kv is not null) return _kv;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_kv is not null) return _kv;
            var ctx = new NatsKVContext(_js);
            // Create-or-update with the TTL; reader and writer both default to DefaultTtlSeconds so the
            // bucket config is consistent (override the TTL on both sides in lockstep — ADR-0004).
            _kv = await ctx.CreateStoreAsync(
                new NatsKVConfig(BucketName) { History = 1, MaxAge = _ttl }, ct).ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
        }
        return _kv!;
    }

    public async Task MarkConnectedAsync(
        string gatewayId, string replicaId, string? appliedRevision = null, CancellationToken ct = default)
    {
        try
        {
            var status = new GatewayConnectionStatus(replicaId, DateTimeOffset.UtcNow, appliedRevision);
            var json = JsonSerializer.SerializeToUtf8Bytes(status);
            var kv = await GetKvAsync(ct).ConfigureAwait(false);
            await kv.PutAsync(SanitizeKey(gatewayId), json, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* stream ending — expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway connection heartbeat write failed for {GatewayId}", ForLog(gatewayId));
        }
    }

    public async Task MarkDisconnectedAsync(string gatewayId, string replicaId, CancellationToken ct = default)
    {
        try
        {
            var kv = await GetKvAsync(ct).ConfigureAwait(false);
            var key = SanitizeKey(gatewayId);
            // Epoch guard: only delete if we still own the entry. If a newer stream (possibly on another
            // replica) already overwrote it, leave it — deleting would falsely flap the gateway to
            // disconnected until the new owner's next heartbeat.
            var current = await ReadAsync(kv, key, ct).ConfigureAwait(false);
            if (current is not null && !string.Equals(current.ReplicaId, replicaId, StringComparison.Ordinal))
                return;
            await kv.DeleteAsync(key, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway connection teardown failed for {GatewayId}", ForLog(gatewayId));
        }
    }

    public async Task<GatewayConnectionStatus?> GetAsync(string gatewayId, CancellationToken ct = default)
    {
        try
        {
            var kv = await GetKvAsync(ct).ConfigureAwait(false);
            return await ReadAsync(kv, SanitizeKey(gatewayId), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway connection read failed for {GatewayId}", ForLog(gatewayId));
            return null;
        }
    }

    private static async Task<GatewayConnectionStatus?> ReadAsync(INatsKVStore kv, string key, CancellationToken ct)
    {
        try
        {
            var entry = await kv.GetEntryAsync<byte[]>(key, cancellationToken: ct).ConfigureAwait(false);
            return entry.Value is null ? null : JsonSerializer.Deserialize<GatewayConnectionStatus>(entry.Value);
        }
        catch (NatsKVKeyNotFoundException)
        {
            return null;
        }
    }

    internal static string SanitizeKey(string gatewayId) => _invalidKey.Replace(gatewayId, "_");

    /// <summary>Neutralises control chars in a gateway_id so it is safe to write to a log sink.</summary>
    internal static string ForLog(string gatewayId) => _controlChars.Replace(gatewayId, "_");
}
