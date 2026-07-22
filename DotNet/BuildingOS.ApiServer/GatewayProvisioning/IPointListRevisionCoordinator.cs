using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace BuildingOs.ApiServer.GatewayProvisioning;

/// <summary>
/// Shared point-list revision registry. The generation changes before a live Twin import, making
/// every previously published ETag stale across all API replicas. A request may return a cache-fast
/// 304 only when its gateway revision belongs to the registry's current generation.
/// </summary>
public interface IPointListRevisionCoordinator
{
    Task<string?> GetGenerationAsync(CancellationToken ct = default);
    Task<string?> GetCurrentEtagAsync(string gatewayId, CancellationToken ct = default);
    Task SaveIfGenerationUnchangedAsync(
        string gatewayId, string etag, string expectedGeneration, CancellationToken ct = default);
    Task<string> BeginUpdateAsync(CancellationToken ct = default);
    Task CompleteUpdateAsync(string updateToken, CancellationToken ct = default);
}

/// <summary>In-process implementation for tests and single-process composition.</summary>
public sealed class MemoryPointListRevisionCoordinator : IPointListRevisionCoordinator
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RevisionEntry> _entries = new(StringComparer.Ordinal);
    private string _generation = NewGeneration();
    private bool _updating;

    public Task<string?> GetGenerationAsync(CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult<string?>(_updating ? null : _generation);
    }

    public Task<string?> GetCurrentEtagAsync(string gatewayId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            return Task.FromResult<string?>(
                !_updating && _entries.TryGetValue(gatewayId, out var entry) && entry.Generation == _generation
                    ? entry.Etag
                    : null);
        }
    }

    public Task SaveIfGenerationUnchangedAsync(
        string gatewayId, string etag, string expectedGeneration, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_updating && _generation == expectedGeneration)
                _entries[gatewayId] = new RevisionEntry(_generation, etag);
        }
        return Task.CompletedTask;
    }

    public Task<string> BeginUpdateAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_updating) throw new InvalidOperationException("A point-list update is already in progress");
            _generation = NewGeneration();
            _updating = true;
            _entries.Clear();
            return Task.FromResult(_generation);
        }
    }

    public Task CompleteUpdateAsync(string updateToken, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_updating || !string.Equals(_generation, updateToken, StringComparison.Ordinal))
                throw new InvalidOperationException("The point-list update token is no longer current");
            _generation = NewGeneration();
            _updating = false;
            _entries.Clear();
        }
        return Task.CompletedTask;
    }

    private static string NewGeneration() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// NATS KV-backed coordinator used in production. Reads fail closed (cache miss), so a KV outage
/// costs a Twin query but can never produce a stale 304. Invalidation is intentionally not swallowed:
/// callers must invalidate successfully before mutating the live Twin.
/// </summary>
public sealed class NatsKvPointListRevisionCoordinator(
    INatsJSContext js,
    ILogger<NatsKvPointListRevisionCoordinator> logger) : IPointListRevisionCoordinator
{
    private const string BucketName = "pointlist-revision";
    private const string GenerationKey = "generation";
    private INatsKVStore? _kv;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public async Task<string?> GetGenerationAsync(CancellationToken ct = default)
    {
        try
        {
            var kv = await GetKvAsync(ct).ConfigureAwait(false);
            return StableGeneration(await ReadGenerationAsync(kv, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Point-list revision generation read failed; bypassing 304 cache");
            return null;
        }
    }

    public async Task<string?> GetCurrentEtagAsync(string gatewayId, CancellationToken ct = default)
    {
        try
        {
            var kv = await GetKvAsync(ct).ConfigureAwait(false);
            var generation = StableGeneration(await ReadGenerationAsync(kv, ct).ConfigureAwait(false));
            if (generation is null) return null;
            var entry = await ReadEntryAsync(kv, GatewayKey(gatewayId), ct).ConfigureAwait(false);
            return entry?.Generation == generation ? entry.Etag : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Point-list revision read failed for gateway; bypassing 304 cache");
            return null;
        }
    }

    public async Task SaveIfGenerationUnchangedAsync(
        string gatewayId, string etag, string expectedGeneration, CancellationToken ct = default)
    {
        try
        {
            var kv = await GetKvAsync(ct).ConfigureAwait(false);
            var current = StableGeneration(await ReadGenerationAsync(kv, ct).ConfigureAwait(false));
            if (current is null || !string.Equals(current, expectedGeneration, StringComparison.Ordinal)) return;
            var json = JsonSerializer.SerializeToUtf8Bytes(new RevisionEntry(current, etag));
            await kv.PutAsync(GatewayKey(gatewayId), json, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Point-list revision write failed for gateway; next request will query Twin");
        }
    }

    public async Task<string> BeginUpdateAsync(CancellationToken ct = default)
    {
        var kv = await GetKvAsync(ct).ConfigureAwait(false);
        var current = await ReadGenerationEntryAsync(kv, ct).ConfigureAwait(false);
        if (StableGeneration(current.Value) is null)
            throw new InvalidOperationException("A point-list update is already in progress");

        var token = NewGeneration();
        await kv.UpdateAsync(
            GenerationKey, Encoding.UTF8.GetBytes($"updating:{token}"), current.Revision,
            cancellationToken: ct).ConfigureAwait(false);
        return token;
    }

    public async Task CompleteUpdateAsync(string updateToken, CancellationToken ct = default)
    {
        var kv = await GetKvAsync(ct).ConfigureAwait(false);
        var current = await ReadGenerationEntryAsync(kv, ct).ConfigureAwait(false);
        if (!string.Equals(current.Value, $"updating:{updateToken}", StringComparison.Ordinal))
            throw new InvalidOperationException("The point-list update token is no longer current");

        await kv.UpdateAsync(
            GenerationKey, Encoding.UTF8.GetBytes($"stable:{NewGeneration()}"), current.Revision,
            cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<INatsKVStore> GetKvAsync(CancellationToken ct)
    {
        if (_kv is not null) return _kv;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_kv is not null) return _kv;
            _kv = await new NatsKVContext(js)
                .CreateStoreAsync(new NatsKVConfig(BucketName) { History = 1 }, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
        }
        return _kv;
    }

    private static async Task<string> ReadGenerationAsync(INatsKVStore kv, CancellationToken ct)
        => (await ReadGenerationEntryAsync(kv, ct).ConfigureAwait(false)).Value;

    private static async Task<GenerationEntry> ReadGenerationEntryAsync(INatsKVStore kv, CancellationToken ct)
    {
        try
        {
            var entry = await kv.GetEntryAsync<byte[]>(GenerationKey, cancellationToken: ct).ConfigureAwait(false);
            if (entry.Value is { Length: > 0 })
                return new GenerationEntry(Encoding.UTF8.GetString(entry.Value), entry.Revision);
        }
        catch (NatsKVKeyNotFoundException) { }

        var generation = $"stable:{NewGeneration()}";
        try
        {
            var revision = await kv.CreateAsync(
                GenerationKey, Encoding.UTF8.GetBytes(generation), cancellationToken: ct).ConfigureAwait(false);
            return new GenerationEntry(generation, revision);
        }
        catch (NatsKVException)
        {
            var entry = await kv.GetEntryAsync<byte[]>(GenerationKey, cancellationToken: ct).ConfigureAwait(false);
            return new GenerationEntry(Encoding.UTF8.GetString(entry.Value!), entry.Revision);
        }
    }

    private static string? StableGeneration(string generation)
        => generation.StartsWith("stable:", StringComparison.Ordinal) ? generation : null;

    private static async Task<RevisionEntry?> ReadEntryAsync(INatsKVStore kv, string key, CancellationToken ct)
    {
        try
        {
            var entry = await kv.GetEntryAsync<byte[]>(key, cancellationToken: ct).ConfigureAwait(false);
            return entry.Value is null ? null : JsonSerializer.Deserialize<RevisionEntry>(entry.Value);
        }
        catch (NatsKVKeyNotFoundException)
        {
            return null;
        }
    }

    private static string GatewayKey(string gatewayId)
        => $"gateway.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gatewayId))).ToLowerInvariant()}";

    private static string NewGeneration() => Guid.NewGuid().ToString("N");
}

internal sealed record RevisionEntry(string Generation, string Etag);
internal sealed record GenerationEntry(string Value, ulong Revision);

/// <summary>Invalidates persisted revisions after startup seed work and before serving traffic.</summary>
public sealed class PointListRevisionStartupInvalidator(
    IPointListRevisionCoordinator revisions) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var token = await revisions.BeginUpdateAsync(ct).ConfigureAwait(false);
        await revisions.CompleteUpdateAsync(token, ct).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
