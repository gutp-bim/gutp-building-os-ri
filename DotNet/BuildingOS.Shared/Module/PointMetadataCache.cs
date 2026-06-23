using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Module;

/// <summary>
/// Process-local cache of point-id → <see cref="PointMetadata"/>. Loads the whole point list from
/// the data source on first use and refreshes lazily on TTL expiry, with retry-on-failure and
/// stale-serving — the same pattern as <see cref="PointIdFactory"/> — so the gRPC ingest path
/// enriches frames without a per-frame graph query.
/// </summary>
public sealed class PointMetadataCache : IPointMetadataCache, IDisposable
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How recently the cache must have been (re)loaded for a cache miss to be served without forcing
    /// a reload (#188). A point added to the twin becomes visible within this window instead of waiting
    /// out the full TTL, while a flood of genuinely-unknown ids triggers at most one reload per window.
    /// </summary>
    public static readonly TimeSpan DefaultMissRefreshInterval = TimeSpan.FromSeconds(30);
    private const int MaxRetries = 5;

    private readonly IPointMetadataDataSource _dataSource;
    private readonly ILogger<PointMetadataCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _missRefreshInterval;
    private readonly TimeSpan _retryBaseDelay;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IReadOnlyDictionary<string, PointMetadata>? _cache;
    private DateTime _cachedAt = DateTime.MinValue;       // last SUCCESSFUL load (data freshness)
    private DateTime _lastAttemptAt = DateTime.MinValue;  // last load ATTEMPT (rate-limits retries)

    internal static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromSeconds(1);

    public PointMetadataCache(
        IPointMetadataDataSource dataSource,
        ILogger<PointMetadataCache> logger,
        TimeSpan? cacheTtl = null,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? missRefreshInterval = null)
    {
        _dataSource = dataSource;
        _logger = logger;
        _ttl = cacheTtl ?? DefaultTtl;
        _missRefreshInterval = missRefreshInterval ?? DefaultMissRefreshInterval;
        _retryBaseDelay = retryBaseDelay ?? DefaultRetryBaseDelay;
    }

    public async Task<PointMetadata?> GetAsync(string pointId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pointId)) return null;

        var cache = await GetOrRefreshCacheAsync(_ttl, cancellationToken).ConfigureAwait(false);
        if (cache.TryGetValue(pointId, out var meta)) return meta;

        // Miss: a point added since the last load would be absent for up to the full TTL. Force a
        // bounded single-flight reload (rate-limited to once per miss-interval so an unknown-id flood
        // cannot stampede the data source), then re-check. Skipped when the interval is not shorter
        // than the TTL (the TTL refresh already covers it).
        if (_missRefreshInterval < _ttl)
        {
            cache = await GetOrRefreshCacheAsync(_missRefreshInterval, cancellationToken).ConfigureAwait(false);
            if (cache.TryGetValue(pointId, out var refreshed)) return refreshed;
        }

        return null;
    }

    public void Dispose() => _lock.Dispose();

    private async Task<IReadOnlyDictionary<string, PointMetadata>> GetOrRefreshCacheAsync(
        TimeSpan maxAge, CancellationToken ct)
    {
        if (_cache is not null && DateTime.UtcNow - _cachedAt < maxAge)
            return _cache;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null && DateTime.UtcNow - _cachedAt < maxAge)
                return _cache;

            // Rate-limit by last ATTEMPT (not last success): when loads keep failing, _cachedAt cannot
            // advance, so without this an outage + miss flood would re-run LoadWithRetryAsync on every
            // miss. Serve the (stale) cache if we already attempted within the window.
            if (_cache is not null && DateTime.UtcNow - _lastAttemptAt < maxAge)
                return _cache;

            _lastAttemptAt = DateTime.UtcNow;
            _logger.LogInformation("Refreshing point metadata cache (maxAge={MaxAge})", maxAge);
            try
            {
                var loaded = await LoadWithRetryAsync(ct).ConfigureAwait(false);
                // Last write wins on duplicate point ids; gateway-id uniqueness is enforced at import.
                _cache = loaded.GroupBy(m => m.PointId)
                               .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
                _cachedAt = DateTime.UtcNow;
                _logger.LogInformation("Point metadata cache refreshed: {Count} entries", _cache.Count);
            }
            catch (Exception ex)
            {
                if (_cache is not null)
                {
                    _logger.LogWarning(ex, "Point metadata refresh failed; serving stale data ({Age:F0}s old)",
                        (DateTime.UtcNow - _cachedAt).TotalSeconds);
                }
                else
                {
                    throw;
                }
            }

            return _cache!;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<PointMetadata[]> LoadWithRetryAsync(CancellationToken ct)
    {
        var delay = _retryBaseDelay;
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await _dataSource.GetAllAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < MaxRetries)
                {
                    _logger.LogWarning(ex,
                        "Point metadata load attempt {Attempt}/{Max} failed, retrying in {Delay}s",
                        attempt, MaxRetries, (int)delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay *= 2;
                }
            }
        }

        _logger.LogError(lastEx, "Point metadata load failed after {Max} attempts", MaxRetries);
        throw lastEx!;
    }
}
