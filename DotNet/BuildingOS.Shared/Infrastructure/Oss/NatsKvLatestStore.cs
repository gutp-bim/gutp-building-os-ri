using System.Text.Json;
using System.Text.RegularExpressions;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// NATS KV-backed hot/latest telemetry store.
/// Bucket "telemetry-latest", history=1 — stores only the most recent value per point.
/// </summary>
public sealed class NatsKvLatestStore : IHotTelemetryStore
{
    private const string BucketName = "telemetry-latest";
    private static readonly Regex _invalidKey = new(@"[^a-zA-Z0-9_.\-]", RegexOptions.Compiled);

    private readonly INatsJSContext _js;
    private readonly ILogger<NatsKvLatestStore> _logger;
    private INatsKVStore? _kv;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public NatsKvLatestStore(INatsJSContext js, ILogger<NatsKvLatestStore> logger)
    {
        _js = js;
        _logger = logger;
    }

    private async Task<INatsKVStore> GetKvAsync(CancellationToken ct)
    {
        if (_kv is not null) return _kv;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_kv is not null) return _kv;
            var ctx = new NatsKVContext(_js);
            _kv = await ctx.CreateStoreAsync(new NatsKVConfig(BucketName) { History = 1 }, ct);
        }
        finally
        {
            _initLock.Release();
        }
        return _kv!;
    }

    public async Task PutAsync(string pointId, ValidTelemetryData data, CancellationToken cancellationToken = default)
    {
        var key = SanitizeKey(pointId);
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        var kv = await GetKvAsync(cancellationToken);
        await kv.PutAsync(key, json, cancellationToken: cancellationToken);
    }

    public async Task<ValidTelemetryData?> GetAsync(string pointId, CancellationToken cancellationToken = default)
    {
        var key = SanitizeKey(pointId);
        try
        {
            var kv = await GetKvAsync(cancellationToken);
            var entry = await kv.GetEntryAsync<byte[]>(key, cancellationToken: cancellationToken);
            if (entry.Value is null) return null;

            var data = JsonSerializer.Deserialize<ValidTelemetryData>(entry.Value);
            // #417: the KV revision's own Created timestamp — when this exact value was written to the
            // hot store — not the row's own Datetime (device/simulated clock). Lets a caller tell a
            // freshly-written value apart from one that has sat unchanged since before, say, its point
            // went temporarily invisible in the twin and is only now being read again.
            if (data is not null) data.IngestedAt = entry.Created.UtcDateTime.ToString("O");
            return data;
        }
        catch (NatsKVKeyNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NatsKvLatestStore.GetAsync failed for point {PointId}", pointId);
            return null;
        }
    }

    internal static string SanitizeKey(string pointId) =>
        _invalidKey.Replace(pointId, "_");
}
