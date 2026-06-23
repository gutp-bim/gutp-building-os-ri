using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Module;

public interface IPointIdFactory
{
    public Task<string[]> GetPointIdAsync(string connectorName, string key);
    public Task<(bool found, string[] pointIds)> TryGetPointIdAsync(string connectorName, string key);
    public Task<(bool found, string localId)> TryGetLocalIdAsync(string pointId);
}

public class PointIdFactory : IPointIdFactory, IDisposable
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How recently the cache must have been (re)loaded for a miss to be served without forcing a
    /// reload (#188). A device added to the twin becomes resolvable within this window instead of
    /// waiting out the full TTL (and falling back to a GUID), while a flood of genuinely-unknown keys
    /// triggers at most one reload per window. Mirrors <see cref="PointMetadataCache"/>.
    /// </summary>
    public static readonly TimeSpan DefaultMissRefreshInterval = TimeSpan.FromSeconds(30);
    private const int MaxRetries = 5;

    private readonly IPointIdDataSource _dataSource;
    private readonly ILogger<PointIdFactory> _logger;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _missRefreshInterval;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private PointIdInfo[]? _cache;
    private DateTime _cachedAt = DateTime.MinValue;       // last SUCCESSFUL load (data freshness)
    private DateTime _lastAttemptAt = DateTime.MinValue;  // last load ATTEMPT (rate-limits retries)

    internal static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _retryBaseDelay;

    public PointIdFactory(
        IPointIdDataSource dataSource,
        ILogger<PointIdFactory> logger,
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

    /// <summary>
    /// 各デバイスのデータソースに、共通的な Id を振る
    ///
    /// 注意: PointIdが見つからない場合はGUIDを生成して返します。
    /// PointIdが見つからない場合の処理を明確にしたい場合は TryGetPointIdAsync を使用してください。
    /// </summary>
    public async Task<string[]> GetPointIdAsync(string connectorName, string key)
    {
        var (found, pointIds) = await TryGetPointIdAsync(connectorName, key);

        if (!found)
        {
            // 後方互換性のためGUIDを生成して返す
            return [Guid.NewGuid().ToString()];
        }

        return pointIds;
    }

    /// <summary>
    /// PointIdを取得し、見つからない場合は明確にfalseを返します
    /// PointIdが存在しない場合にデータを保存したくない場合はこのメソッドを使用してください
    /// </summary>
    public async Task<(bool found, string[] pointIds)> TryGetPointIdAsync(string connectorName, string key)
    {
        var cache = await GetOrRefreshCacheAsync(_ttl);
        var pointIds = SelectPointIds(cache, key);

        // Miss: a key added since the last load would be absent for up to the full TTL (and resolve to
        // a GUID). Force a bounded single-flight reload (rate-limited per miss-interval), then re-check.
        if (pointIds.Length == 0)
        {
            cache = await RefreshOnMissAsync(cache);
            pointIds = SelectPointIds(cache, key);
        }

        if (pointIds.Length > 0)
        {
            _logger.LogDebug("Cache hit: {ConnectorName}/{Key} → {Count} point(s)", connectorName, key, pointIds.Length);
            return (true, pointIds);
        }

        _logger.LogDebug("Cache miss: {ConnectorName}/{Key}", connectorName, key);
        return (false, []);
    }

    public async Task<(bool found, string localId)> TryGetLocalIdAsync(string pointId)
    {
        var cache = await GetOrRefreshCacheAsync(_ttl);
        var info = cache.FirstOrDefault(x => x.PointId == pointId);
        if (info is null)
        {
            cache = await RefreshOnMissAsync(cache);
            info = cache.FirstOrDefault(x => x.PointId == pointId);
        }
        return info is not null ? (true, info.Key) : (false, string.Empty);
    }

    public void Dispose() => _lock.Dispose();

    private static string[] SelectPointIds(PointIdInfo[] cache, string key)
        => cache.Where(x => x.Key == key).Select(x => x.PointId).ToArray();

    // Bounded single-flight reload on a cache miss: only reloads when the cache is older than the
    // miss-interval, so an unknown-key flood cannot stampede the data source. Skipped when the interval
    // is not shorter than the TTL (the TTL refresh already covers it).
    private async Task<PointIdInfo[]> RefreshOnMissAsync(PointIdInfo[] current)
        => _missRefreshInterval < _ttl
            ? await GetOrRefreshCacheAsync(_missRefreshInterval).ConfigureAwait(false)
            : current;

    private async Task<PointIdInfo[]> GetOrRefreshCacheAsync(TimeSpan maxAge)
    {
        // Fast path: cache is still fresh enough
        if (_cache is not null && DateTime.UtcNow - _cachedAt < maxAge)
            return _cache;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Double-check inside lock
            if (_cache is not null && DateTime.UtcNow - _cachedAt < maxAge)
                return _cache;

            // Rate-limit by last ATTEMPT (not last success): when loads keep failing, _cachedAt cannot
            // advance, so without this an outage + miss flood would re-run LoadWithRetryAsync on every
            // miss. Serve the (stale) cache if we already attempted within the window.
            if (_cache is not null && DateTime.UtcNow - _lastAttemptAt < maxAge)
                return _cache;

            _lastAttemptAt = DateTime.UtcNow;
            _logger.LogInformation("Refreshing PointId cache (maxAge={MaxAge})", maxAge);
            try
            {
                _cache = await LoadWithRetryAsync().ConfigureAwait(false);
                _cachedAt = DateTime.UtcNow;
                _logger.LogInformation("PointId cache refreshed: {Count} entries loaded", _cache.Length);
            }
            catch (Exception ex)
            {
                if (_cache is not null)
                {
                    _logger.LogWarning(ex, "PointId cache refresh failed; serving stale data ({Age:F0}s old)",
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

    private async Task<PointIdInfo[]> LoadWithRetryAsync()
    {
        var delay = _retryBaseDelay;
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await _dataSource.GetPointIdInfosAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < MaxRetries)
                {
                    _logger.LogWarning(ex,
                        "PointId data source load attempt {Attempt}/{Max} failed, retrying in {Delay}s",
                        attempt, MaxRetries, (int)delay.TotalSeconds);
                    await Task.Delay(delay).ConfigureAwait(false);
                    delay *= 2;
                }
            }
        }

        _logger.LogError(lastEx, "PointId data source load failed after {Max} attempts", MaxRetries);
        throw lastEx!;
    }
}
