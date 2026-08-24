using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Module;

/// <summary>
/// Process-local cache of point-id → <see cref="PointMetadata"/>. Loads the whole point list from
/// the data source on first use, so the gRPC ingest path enriches frames without a per-frame graph
/// query.
/// <para>
/// <b>Shared, caller-independent loads (#371).</b> This cache is registered as a <i>singleton</i> and
/// is read by every concurrent gRPC ingress stream, so a load started on behalf of one caller is
/// really shared infrastructure. The load therefore runs on the cache's own lifetime
/// <see cref="CancellationToken"/> — never on a caller-supplied one — and a caller's token can only
/// cancel <i>that caller's wait</i>. Propagating the per-request gRPC token into the shared load is
/// what caused #371: the soak client re-established its stream every 300 s — the same as the default
/// TTL, so teardown landed on the refresh boundary nearly every time — and the ending stream's token
/// aborted the refresh that every other stream was parked behind on the shared lock. Because the
/// aborted load never published a snapshot, the next window blocked on it all over again, and frames
/// queued behind it until the client's Ack timed out (21 of 46 chunks).
/// </para>
/// <para>
/// <b>Stale-while-revalidate on TTL expiry.</b> Once the cache is warm, a TTL-expired read returns the
/// stale entry <i>immediately</i> and revalidates in the background (single-flight, and rate-limited
/// by the last load ATTEMPT so a hot path cannot spawn a refresh per frame). Only a cold cache blocks.
/// The #188 miss path is deliberately the exception: it stays synchronous — see
/// <see cref="GetAsync"/>.
/// </para>
/// <para>
/// A refresh that fails with a warm cache logs a warning and keeps serving stale; a load that fails
/// with a cold cache throws to the waiting callers. A refresh aborted by <see cref="Dispose"/> is
/// <i>not</i> a failure — it is logged as shutdown, so the #371 warning signature (a cancelled shared
/// load) does not fire on every pod restart.
/// </para>
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

    /// <summary>Guards <see cref="_inFlight"/> and <see cref="_lastAttemptAt"/>. Held only for short, synchronous sections.</summary>
    private readonly object _sync = new();

    /// <summary>
    /// The cache's own lifetime token source: every data-source load and every retry backoff runs on
    /// it, so no caller can abort work the other callers depend on (#371). Cancelled by <see cref="Dispose"/>.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    /// <summary>Immutable snapshot so the hot path can read entries + age together without locking.</summary>
    private volatile Snapshot? _snapshot;

    /// <summary>The shared in-flight load, published under <see cref="_sync"/> and cleared when it settles.</summary>
    private Task<IReadOnlyDictionary<string, PointMetadata>>? _inFlight;

    private DateTime _lastAttemptAt = DateTime.MinValue;  // last load ATTEMPT (rate-limits retries)
    private bool _disposed;

    internal static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromSeconds(1);

    private sealed record Snapshot(IReadOnlyDictionary<string, PointMetadata> Entries, DateTime LoadedAt);

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
        _lifetimeToken = _lifetime.Token;
    }

    public async Task<PointMetadata?> GetAsync(string pointId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pointId)) return null;

        // TTL freshness: stale-while-revalidate. A warm-but-expired cache answers now and revalidates
        // in the background; only a cold cache makes the caller wait (#371).
        var cache = await GetOrRefreshCacheAsync(_ttl, serveStaleWhileRevalidating: true, cancellationToken)
            .ConfigureAwait(false);
        if (cache.TryGetValue(pointId, out var meta)) return meta;

        // Miss: a point added since the last load would be absent for up to the full TTL. Force a
        // bounded single-flight reload (rate-limited to once per miss-interval so an unknown-id flood
        // cannot stampede the data source), then re-check.
        //
        // This path stays SYNCHRONOUS on purpose: serving it stale-while-revalidate would drop the
        // very first frame of every newly-added point, and at a per-point cadence measured in minutes
        // that is minutes of data lost per point.
        //
        // It is deliberately unconditional. It used to be skipped when the miss interval was not
        // shorter than the TTL, on the grounds that "the TTL refresh already covers it" — which stopped
        // being true when the TTL path became stale-while-revalidate (#371): that path no longer blocks,
        // so deferring to it would silently forfeit the #188 guarantee for any such configuration.
        // Running it always costs nothing when the snapshot is fresher than the miss interval (the age
        // check below returns immediately) and cannot stampede, because the reload is rate-limited on
        // the last load ATTEMPT regardless of which path asked for it.
        cache = await GetOrRefreshCacheAsync(_missRefreshInterval, serveStaleWhileRevalidating: false, cancellationToken)
            .ConfigureAwait(false);
        if (cache.TryGetValue(pointId, out var refreshed)) return refreshed;

        return null;
    }

    /// <summary>
    /// Cancels the lifetime token — aborting any in-flight load — and disposes it. Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Cancel before disposing: an in-flight load observes the cancellation, and a registration
        // raced onto an already-cancelled token runs inline instead of touching the disposed source.
        try
        {
            _lifetime.Cancel();
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(ex, "Point metadata cache disposal: a cancellation callback threw");
        }

        _lifetime.Dispose();
    }

    /// <summary>
    /// Returns the best entries available, starting a shared single-flight reload when the snapshot is
    /// older than <paramref name="maxAge"/> and no load has been ATTEMPTED within that window.
    /// <para>
    /// <b>The result may be older than <paramref name="maxAge"/>.</b> A warm snapshot is returned as-is
    /// when <paramref name="serveStaleWhileRevalidating"/> is set (the TTL path — the reload lands in the
    /// background), and also, on either path, when the last load <i>attempt</i> was within
    /// <paramref name="maxAge"/> (rate limiting, so an unknown-id flood cannot stampede the data source).
    /// Only a cold cache is guaranteed to have waited for a fresh load.
    /// </para>
    /// <paramref name="cancellationToken"/> bounds only this caller's wait — never the shared load.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, PointMetadata>> GetOrRefreshCacheAsync(
        TimeSpan maxAge, bool serveStaleWhileRevalidating, CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        if (snapshot is not null && DateTime.UtcNow - snapshot.LoadedAt < maxAge)
            return snapshot.Entries;

        Task<IReadOnlyDictionary<string, PointMetadata>> inFlight;
        lock (_sync)
        {
            snapshot = _snapshot;
            var now = DateTime.UtcNow;
            if (snapshot is not null && now - snapshot.LoadedAt < maxAge)
                return snapshot.Entries;

            if (_inFlight is null)
            {
                // Rate-limit by last ATTEMPT (not last success): when loads keep failing, the snapshot's
                // age cannot advance, so without this an outage + miss flood would re-run the retry
                // sequence on every miss. It is also what stops the stale-while-revalidate hot path from
                // kicking off a fresh background load on every frame during the expired window.
                if (snapshot is not null && now - _lastAttemptAt < maxAge)
                    return snapshot.Entries;

                _lastAttemptAt = now;
                _inFlight = StartRefreshLocked(maxAge);
            }

            inFlight = _inFlight;

            // Warm cache + TTL expiry: answer from stale data now, let the refresh land in the background.
            if (serveStaleWhileRevalidating && snapshot is not null)
                return snapshot.Entries;
        }

        // Only this caller's wait is cancellable; the load itself keeps running for everyone else.
        return await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes a completion source for the shared load, then starts the load off the current stack so
    /// it can never re-enter <see cref="_sync"/> synchronously while the starter still holds it.
    /// Must be called under <see cref="_sync"/>.
    /// </summary>
    private Task<IReadOnlyDictionary<string, PointMetadata>> StartRefreshLocked(TimeSpan maxAge)
    {
        var completion = new TaskCompletionSource<IReadOnlyDictionary<string, PointMetadata>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // RefreshAsync settles `completion` on every path, but a throw from inside its own catch block
        // (an ILogger provider that faults) would still fault this task, which nothing else observes.
        _ = Task.Run(() => RefreshAsync(completion, maxAge), CancellationToken.None)
                .ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

        // A background revalidation has no waiter, so nothing would otherwise observe a fault.
        _ = completion.Task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return completion.Task;
    }

    /// <summary>
    /// Clears the shared in-flight slot, but only when it still holds <paramref name="load"/>: a
    /// settling refresh must never null out a newer one another caller has already published.
    /// </summary>
    private void ClearInFlight(Task<IReadOnlyDictionary<string, PointMetadata>> load)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_inFlight, load)) _inFlight = null;
        }
    }

    private async Task RefreshAsync(
        TaskCompletionSource<IReadOnlyDictionary<string, PointMetadata>> completion, TimeSpan maxAge)
    {
        try
        {
            _logger.LogInformation("Refreshing point metadata cache (maxAge={MaxAge})", maxAge);
            var loaded = await LoadWithRetryAsync(_lifetimeToken).ConfigureAwait(false);
            // Last write wins on duplicate point ids; gateway-id uniqueness is enforced at import.
            var entries = loaded.GroupBy(m => m.PointId)
                                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

            _snapshot = new Snapshot(entries, DateTime.UtcNow);
            ClearInFlight(completion.Task);
            // Release the waiters BEFORE logging: every concurrent caller is parked on this completion
            // source, and ILogger.Log is not exception-free (a provider throw is rethrown to the caller),
            // so a log that throws here would strand them all until their own request tokens fire.
            completion.TrySetResult(entries);

            _logger.LogInformation("Point metadata cache refreshed: {Count} entries", entries.Count);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            // Shutdown, not a failure. Logging this as a warning carrying a TaskCanceledException would
            // reproduce the #371 signature on every restart and make a genuine recurrence unreadable.
            ClearInFlight(completion.Task);
            completion.TrySetCanceled(_lifetimeToken);
            _logger.LogInformation("Point metadata refresh cancelled: the cache is disposing");
        }
        catch (Exception ex)
        {
            var stale = _snapshot;
            ClearInFlight(completion.Task);

            if (stale is not null)
            {
                completion.TrySetResult(stale.Entries);  // release the waiters before logging (see above)
                _logger.LogWarning(ex, "Point metadata refresh failed; serving stale data ({Age:F0}s old)",
                    (DateTime.UtcNow - stale.LoadedAt).TotalSeconds);
            }
            else
            {
                completion.TrySetException(ex);
            }
        }
        finally
        {
            // Belt and braces: no path may leave the shared load pending — waiters would hang until
            // their own tokens fired, with a perfectly good snapshot sitting in _snapshot.
            if (!completion.Task.IsCompleted)
            {
                ClearInFlight(completion.Task);
                completion.TrySetException(new InvalidOperationException(
                    "Point metadata refresh terminated unexpectedly"));
            }
        }
    }

    /// <summary>
    /// Loads the point list, retrying with exponential backoff. <paramref name="ct"/> is always the
    /// cache's lifetime token (#371) — a caller-supplied token here would let one departing stream
    /// abort both the load and the backoff for everyone else.
    /// </summary>
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the cache itself is shutting down — retrying would be pointless
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
