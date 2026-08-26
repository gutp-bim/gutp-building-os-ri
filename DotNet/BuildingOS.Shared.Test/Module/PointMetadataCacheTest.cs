using BuildingOS.Shared.Module;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Module;

public class PointMetadataCacheTest
{
    private static PointMetadata Meta(string pointId, string name = "n")
        => new(pointId, Building: "b", Name: name, DeviceId: "d", GatewayId: "gw");

    private sealed class FakeDataSource : IPointMetadataDataSource
    {
        private volatile PointMetadata[] _data;
        public int Calls { get; private set; }

        public FakeDataSource(params PointMetadata[] data) => _data = data;
        public void SetData(params PointMetadata[] data) => _data = data;

        public Task<PointMetadata[]> GetAllAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_data);
        }
    }

    /// <summary>
    /// A data source the test can hold open, fail on demand, and interrogate for the token it was
    /// handed. It observes that token the way a real HTTP/SPARQL client does — cancelling it aborts
    /// the in-flight load — which is exactly how the caller's per-request gRPC token killed the
    /// shared refresh in #371.
    /// </summary>
    private sealed class GatedDataSource : IPointMetadataDataSource
    {
        private readonly object _sync = new();
        private PointMetadata[] _data;
        private TaskCompletionSource<bool>? _gate;
        private TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _failCount; // -1 = fail every call
        private CancellationToken _lastToken;

        public GatedDataSource(params PointMetadata[] data) => _data = data;

        public int Calls { get { lock (_sync) return _calls; } }

        /// <summary>The <see cref="CancellationToken"/> handed to the most recent load.</summary>
        public CancellationToken LastToken { get { lock (_sync) return _lastToken; } }

        /// <summary>Completes when a load has entered the data source (reset by <see cref="Block"/>).</summary>
        public Task Entered { get { lock (_sync) return _entered.Task; } }

        public void SetData(params PointMetadata[] data) { lock (_sync) _data = data; }

        public void FailNext(int count) { lock (_sync) _failCount = count; }

        public void FailAlways() { lock (_sync) _failCount = -1; }

        /// <summary>Arms the gate: loads block until <see cref="Open"/> is called.</summary>
        public void Block()
        {
            lock (_sync)
            {
                _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>Releases the gate; subsequent loads complete immediately.</summary>
        public void Open()
        {
            TaskCompletionSource<bool>? gate;
            lock (_sync)
            {
                gate = _gate;
                _gate = null;
            }
            gate?.TrySetResult(true);
        }

        public async Task<PointMetadata[]> GetAllAsync(CancellationToken cancellationToken = default)
        {
            Task gate;
            lock (_sync)
            {
                _calls++;
                _lastToken = cancellationToken;
                gate = _gate?.Task ?? Task.CompletedTask;
                _entered.TrySetResult(true);
            }

            if (!gate.IsCompleted)
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (_failCount != 0)
                {
                    if (_failCount > 0) _failCount--;
                    throw new InvalidOperationException("point metadata data source unavailable");
                }
                return _data;
            }
        }
    }

    /// <summary>
    /// Records what was logged and can be armed to THROW from <see cref="Log"/> — which real providers
    /// do: <c>Microsoft.Extensions.Logging.Logger.Log</c> collects a provider's exception and rethrows
    /// it, and the ConnectorWorker runs an OTLP logging pipeline.
    /// </summary>
    private sealed class RecordingLogger : ILogger<PointMetadataCache>
    {
        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

        private readonly List<Entry> _entries = new();
        private readonly Func<string, bool>? _throwOn;

        public RecordingLogger(Func<string, bool>? throwOn = null) => _throwOn = throwOn;

        public IReadOnlyList<Entry> Entries { get { lock (_entries) return _entries.ToArray(); } }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_entries) _entries.Add(new Entry(logLevel, message, exception));
            if (_throwOn?.Invoke(message) == true)
                throw new AggregateException("An error occurred while writing to logger(s).");
        }
    }

    private static PointMetadataCache Build(
        IPointMetadataDataSource source,
        TimeSpan? ttl = null,
        TimeSpan? missInterval = null,
        TimeSpan? retryBaseDelay = null,
        ILogger<PointMetadataCache>? logger = null)
        => new(source, logger ?? NullLogger<PointMetadataCache>.Instance,
            cacheTtl: ttl ?? TimeSpan.FromMinutes(10),
            retryBaseDelay: retryBaseDelay ?? TimeSpan.Zero,
            missRefreshInterval: missInterval ?? TimeSpan.FromSeconds(30));

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, string because, int timeoutMs = 2000)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        Assert.True(completed == (Task)task, because);
        return await task.ConfigureAwait(false);
    }

    private static async Task WithTimeoutAsync(Task task, string because, int timeoutMs = 2000)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        Assert.True(completed == task, because);
        await task.ConfigureAwait(false);
    }

    private static async Task WaitForAsync(Func<bool> condition, string because, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.True(condition(), because);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition().ConfigureAwait(false)) return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.True(await condition().ConfigureAwait(false), because);
    }

    /// <summary>Awaits a caller whose own wait is expected to be cancelled/failed; the outcome is not the assertion.</summary>
    private static async Task SwallowAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* the abandoning caller's own outcome is irrelevant here */ }
    }

    [Fact]
    public async Task GetAsync_ReturnsKnownPoint_FromInitialLoad()
    {
        var source = new FakeDataSource(Meta("PT1"));
        var cache = Build(source);

        var meta = await cache.GetAsync("PT1");

        Assert.NotNull(meta);
        Assert.Equal("PT1", meta!.PointId);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task GetAsync_RefreshesOnMiss_PicksUpNewlyAddedPoint()
    {
        // #188: a point added to the twin after the last load must not be skipped for up to the full
        // TTL. With a zero miss-interval the miss triggers an immediate single-flight reload.
        var source = new FakeDataSource(); // initially empty
        var cache = Build(source, missInterval: TimeSpan.Zero);

        Assert.Null(await cache.GetAsync("PT-NEW")); // miss → reload (still empty)
        source.SetData(Meta("PT-NEW"));              // point added to the twin

        var meta = await cache.GetAsync("PT-NEW");   // miss → reload → now present
        Assert.NotNull(meta);
        Assert.Equal("PT-NEW", meta!.PointId);
    }

    [Fact]
    public async Task GetAsync_MissRefresh_IsRateLimited_AgainstUnknownIdFlood()
    {
        // A flood of genuinely-unknown ids must not stampede the data source: at most one reload per
        // miss-interval. With a long miss-interval the freshly-loaded cache serves all misses.
        var source = new FakeDataSource(Meta("PT1"));
        var cache = Build(source, ttl: TimeSpan.FromMinutes(10), missInterval: TimeSpan.FromMinutes(5));

        for (var i = 0; i < 50; i++)
            Assert.Null(await cache.GetAsync($"PT-UNKNOWN-{i}"));

        // Only the initial load — the miss path saw a fresh cache and did not reload.
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task GetAsync_KnownPoint_DoesNotTriggerMissRefresh()
    {
        var source = new FakeDataSource(Meta("PT1"));
        var cache = Build(source, missInterval: TimeSpan.Zero);

        await cache.GetAsync("PT1");
        await cache.GetAsync("PT1");

        Assert.Equal(1, source.Calls); // served from cache, no reload
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_ForEmptyPointId()
    {
        var source = new FakeDataSource(Meta("PT1"));
        var cache = Build(source);

        Assert.Null(await cache.GetAsync(""));
        Assert.Equal(0, source.Calls); // no load for an empty id
    }

    // ---------------------------------------------------------------------------------------------
    // #371 — the cache is a SINGLETON shared by every gRPC ingress stream. A per-request token must
    // never be able to abort the shared load. Sections A–E of the fix design.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A: an in-flight shared load survives the initiating caller's cancellation.</summary>
    [Fact]
    public async Task GetAsync_InFlightLoad_SurvivesCallerCancellation_AndIsNotReloaded()
    {
        var source = new GatedDataSource(Meta("PT1"));
        source.Block();
        var cache = Build(source);

        using var callerCts = new CancellationTokenSource();
        var abandoned = cache.GetAsync("PT1", callerCts.Token);
        await WithTimeoutAsync(source.Entered, "the cold-start load should have reached the data source");

        // The gRPC stream ends: this caller goes away, every other stream still needs the load.
        callerCts.Cancel();
        await SwallowAsync(abandoned);

        source.Open();

        var meta = await WithTimeoutAsync(
            cache.GetAsync("PT1", CancellationToken.None),
            "a later caller should see the completed load instead of hanging");

        Assert.NotNull(meta);
        Assert.Equal("PT1", meta!.PointId);
        Assert.Equal(1, source.Calls); // the shared load ran to completion; it was NOT re-run
    }

    /// <summary>A: the load never receives a caller-supplied token.</summary>
    [Fact]
    public async Task GetAsync_DataSourceToken_IsNotTheCallersToken()
    {
        var source = new GatedDataSource(Meta("PT1"));
        source.Block();
        var cache = Build(source);

        using var callerCts = new CancellationTokenSource();
        var abandoned = cache.GetAsync("PT1", callerCts.Token);
        await WithTimeoutAsync(source.Entered, "the cold-start load should have reached the data source");

        var handedToTheDataSource = source.LastToken;
        callerCts.Cancel();

        Assert.False(
            handedToTheDataSource.IsCancellationRequested,
            "the token handed to IPointMetadataDataSource.GetAllAsync must be the cache's own lifetime "
            + "token, so cancelling one caller's token cannot abort the shared load");

        source.Open();
        await SwallowAsync(abandoned);
    }

    /// <summary>C: TTL expiry with a warm cache serves stale immediately and revalidates in background.</summary>
    [Fact]
    public async Task GetAsync_TtlExpiredWithWarmCache_ServesStaleImmediately_ThenRefreshesInBackground()
    {
        var source = new GatedDataSource(Meta("PT1", name: "v1"));
        // missInterval >= ttl so the #188 miss path stays out of the way (and PT1 is a hit anyway).
        var cache = Build(source, ttl: TimeSpan.FromMilliseconds(50), missInterval: TimeSpan.FromMinutes(5));

        var first = await cache.GetAsync("PT1");
        Assert.Equal("v1", first!.Name);
        Assert.Equal(1, source.Calls);

        source.Block();                              // the revalidation will hang
        source.SetData(Meta("PT1", name: "v2"));     // the twin changed
        await Task.Delay(120);                       // TTL expired

        var stale = await WithTimeoutAsync(
            cache.GetAsync("PT1", CancellationToken.None),
            "a TTL-expired warm cache must serve the STALE entry immediately instead of blocking the "
            + "hot path on the revalidation (this is the Ack-timeout spike in #371)");
        Assert.NotNull(stale);
        Assert.Equal("v1", stale!.Name);

        await WithTimeoutAsync(source.Entered, "the background revalidation should have been kicked off");
        Assert.Equal(2, source.Calls);

        source.Open();
        await WaitUntilAsync(
            async () => (await cache.GetAsync("PT1", CancellationToken.None))?.Name == "v2",
            "once the background revalidation completes the fresh value must be served");
    }

    /// <summary>B: concurrent cold-start callers share one load (regression guard).</summary>
    [Fact]
    public async Task GetAsync_ConcurrentColdStart_LoadsOnlyOnce()
    {
        var source = new GatedDataSource(Meta("PT1"));
        source.Block();
        var cache = Build(source);

        var callers = Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync("PT1", CancellationToken.None))
            .ToArray();

        await WithTimeoutAsync(source.Entered, "the cold-start load should have reached the data source");
        source.Open();

        var results = await WithTimeoutAsync(Task.WhenAll(callers), "every waiter should be released by the shared load");

        Assert.All(results, m => Assert.Equal("PT1", m!.PointId));
        Assert.Equal(1, source.Calls);
    }

    /// <summary>A: the retry backoff delay is the cache's, not the caller's.</summary>
    [Fact]
    public async Task LoadWithRetry_BackoffDelay_IsNotCancelledByCallerToken()
    {
        var source = new GatedDataSource(Meta("PT1"));
        source.FailNext(1); // attempt 1 fails → the cache enters its backoff delay
        var cache = Build(source, retryBaseDelay: TimeSpan.FromMilliseconds(200));

        using var callerCts = new CancellationTokenSource();
        var abandoned = cache.GetAsync("PT1", callerCts.Token);
        await WithTimeoutAsync(source.Entered, "attempt 1 should have reached the data source");

        callerCts.Cancel(); // the stream ends while the cache is sleeping out its backoff
        await SwallowAsync(abandoned);

        await WaitForAsync(
            () => source.Calls >= 2,
            "the retry backoff must use the cache's own token — attempt 2 never ran because the "
            + "abandoning caller's token cancelled Task.Delay");

        var meta = await WithTimeoutAsync(
            cache.GetAsync("PT1", CancellationToken.None),
            "the retried load should have populated the cache");
        Assert.NotNull(meta);
        Assert.Equal(2, source.Calls); // exactly the failed attempt + the successful retry
    }

    /// <summary>A: Dispose() cancels the cache's own lifetime token.</summary>
    [Fact]
    public async Task Dispose_CancelsLifetimeToken_ObservedByInFlightLoad()
    {
        var source = new GatedDataSource(Meta("PT1"));
        source.Block();
        var cache = Build(source);

        var inFlight = cache.GetAsync("PT1", CancellationToken.None);
        await WithTimeoutAsync(source.Entered, "the cold-start load should have reached the data source");
        var handedToTheDataSource = source.LastToken;

        cache.Dispose();

        Assert.True(
            handedToTheDataSource.IsCancellationRequested,
            "Dispose() must cancel the cache-owned CancellationTokenSource that backs every load");

        source.Open();
        await SwallowAsync(inFlight);
    }

    /// <summary>
    /// D: the #188 miss refresh must also hold when the miss interval is NOT shorter than the TTL.
    /// The miss path used to be skipped in that configuration, deferring to the TTL refresh — which
    /// stopped covering it the moment the TTL path became stale-while-revalidate (#371), since that
    /// path no longer blocks. A newly-added point must still resolve on the same call.
    /// </summary>
    [Theory]
    [InlineData(100, 100)]  // missInterval == ttl
    [InlineData(100, 50)]   // missInterval  >  ttl
    public async Task GetAsync_MissRefresh_IsSynchronous_EvenWhenTheMissIntervalIsNotShorterThanTheTtl(
        int missIntervalMs, int ttlMs)
    {
        var source = new GatedDataSource(Meta("PT1"));
        var cache = Build(
            source,
            ttl: TimeSpan.FromMilliseconds(ttlMs),
            missInterval: TimeSpan.FromMilliseconds(missIntervalMs));

        Assert.NotNull(await cache.GetAsync("PT1", CancellationToken.None));
        Assert.Equal(1, source.Calls);

        source.SetData(Meta("PT1"), Meta("PT2")); // PT2 added to the twin
        await Task.Delay(missIntervalMs + 50);    // both windows are now expired

        var meta = await WithTimeoutAsync(
            cache.GetAsync("PT2", CancellationToken.None),
            "the miss path must reload and resolve the new point on this call");

        Assert.NotNull(meta);
        Assert.Equal("PT2", meta!.PointId);
    }

    /// <summary>D: the #188 miss refresh stays SYNCHRONOUS (regression guard).</summary>
    [Fact]
    public async Task GetAsync_MissRefresh_BlocksUntilReloadCompletes_AndReturnsTheNewPointOnTheSameCall()
    {
        var source = new GatedDataSource(Meta("PT1"));
        var cache = Build(source, ttl: TimeSpan.FromMinutes(10), missInterval: TimeSpan.Zero);

        Assert.NotNull(await cache.GetAsync("PT1"));
        Assert.Equal(1, source.Calls);

        source.Block();
        source.SetData(Meta("PT1"), Meta("PT2")); // PT2 added to the twin

        var pending = cache.GetAsync("PT2", CancellationToken.None);
        await WithTimeoutAsync(source.Entered, "the miss should have forced a reload");

        Assert.False(
            pending.IsCompleted,
            "the miss path must AWAIT the reload — making it async would drop the first frame of every "
            + "newly-added point");

        source.Open();
        var meta = await WithTimeoutAsync(pending, "the miss reload should have completed");
        Assert.NotNull(meta);
        Assert.Equal("PT2", meta!.PointId);
        Assert.Equal(2, source.Calls);
    }

    /// <summary>D + E: miss rate limiting is keyed on the last ATTEMPT, so a failing source cannot be stampeded.</summary>
    [Fact]
    public async Task GetAsync_MissRefresh_RateLimitedByLastAttempt_WhenTheDataSourceIsFailing()
    {
        var source = new GatedDataSource(Meta("PT1"));
        var cache = Build(source, ttl: TimeSpan.FromMinutes(10), missInterval: TimeSpan.FromMilliseconds(100));

        Assert.NotNull(await cache.GetAsync("PT1"));
        Assert.Equal(1, source.Calls);

        source.FailAlways();
        await Task.Delay(150); // the cache is now older than the miss interval → misses will attempt a reload

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
            Assert.Null(await cache.GetAsync($"PT-UNKNOWN-{i}", CancellationToken.None));
        sw.Stop();

        // Without attempt-keyed rate limiting each of the 50 misses would run a full 5-attempt retry
        // sequence (~251 loads) against an already-failing OxiGraph. With it, the attempts are bounded
        // by how many miss-intervals the loop SPANS, not by how many misses it makes — so the ceiling
        // is derived from the measured elapsed time rather than hard-coded, which would turn
        // thread-pool contention on a loaded CI box into a spurious failure.
        const int retriesPerAttempt = 5; // PointMetadataCache.MaxRetries
        var windowsSpanned = (int)(sw.Elapsed.TotalMilliseconds / 100) + 2; // partial window + slack
        Assert.InRange(source.Calls, 2, 1 + (retriesPerAttempt * windowsSpanned));

        // …and independently of the timing: nowhere near one reload sequence per miss.
        Assert.True(source.Calls < 50,
            $"50 misses must not each trigger their own reload sequence (calls={source.Calls})");

        // …and the warm cache is still served throughout.
        Assert.NotNull(await cache.GetAsync("PT1", CancellationToken.None));
    }

    /// <summary>
    /// E: refresh failure with a warm cache serves stale to the waiting caller (regression guard).
    /// The caller must actually AWAIT the failing refresh, so the miss path is used: on the TTL path a
    /// warm cache is returned from inside the lock and the refresh outcome is never observed, which
    /// would make this test pass even if the warm-failure branch faulted the completion instead.
    /// </summary>
    [Fact]
    public async Task GetAsync_RefreshFailure_WithWarmCache_ServesStale()
    {
        var source = new GatedDataSource(Meta("PT1", name: "v1"));
        var cache = Build(source, ttl: TimeSpan.FromMinutes(10), missInterval: TimeSpan.FromMilliseconds(100));

        Assert.Equal("v1", (await cache.GetAsync("PT1"))!.Name);

        source.FailAlways();
        await Task.Delay(150); // older than the miss interval → a miss now forces a (failing) reload

        // The miss path awaits the refresh: it must be released with the stale entries, not the fault.
        var unknown = await WithTimeoutAsync(
            cache.GetAsync("PT-UNKNOWN", CancellationToken.None),
            "a failed refresh with a warm cache must release the waiting caller with stale data — "
            + "returning null for an unknown id — instead of throwing or hanging");
        Assert.Null(unknown);
        Assert.True(source.Calls > 1, "the miss should have attempted a reload");

        var stale = await WithTimeoutAsync(
            cache.GetAsync("PT1", CancellationToken.None),
            "the warm cache must survive the failed refresh");
        Assert.NotNull(stale);
        Assert.Equal("v1", stale!.Name);
    }

    /// <summary>
    /// B/E: the shared completion is settled BEFORE the log call, so a throwing ILogger provider cannot
    /// leave every waiter parked on a completion source that is never completed.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenTheSuccessLogThrows_TheWaitersAreStillReleased()
    {
        var source = new GatedDataSource(Meta("PT1", name: "v1"));
        source.Block();
        // A provider broken for every line the refresh writes once the load has returned — including the
        // stale-serving warning, which is what the "log first" ordering falls through to.
        var logger = new RecordingLogger(throwOn: m =>
            m.Contains("refreshed", StringComparison.Ordinal) || m.Contains("serving stale", StringComparison.Ordinal));
        var cache = Build(source, logger: logger);

        var callers = Enumerable.Range(0, 4)
            .Select(_ => cache.GetAsync("PT1", CancellationToken.None))
            .ToArray();
        await WithTimeoutAsync(source.Entered, "the cold-start load should have reached the data source");
        source.Open();

        var results = await WithTimeoutAsync(
            Task.WhenAll(callers),
            "a logger that throws after the load succeeded must not strand the waiters — the loaded "
            + "entries are already published, so the completion must be settled before logging");
        Assert.All(results, m => Assert.Equal("v1", m!.Name));
    }

    /// <summary>E: same guarantee on the warm-failure path, where the stale-serving warning is logged.</summary>
    [Fact]
    public async Task RefreshAsync_WhenTheStaleWarningThrows_TheWaitersAreStillReleased()
    {
        var source = new GatedDataSource(Meta("PT1", name: "v1"));
        var logger = new RecordingLogger(throwOn: m => m.Contains("serving stale", StringComparison.Ordinal));
        var cache = Build(source, ttl: TimeSpan.FromMinutes(10), missInterval: TimeSpan.FromMilliseconds(100),
            logger: logger);

        Assert.Equal("v1", (await cache.GetAsync("PT1"))!.Name);

        source.FailAlways();
        await Task.Delay(150);

        var unknown = await WithTimeoutAsync(
            cache.GetAsync("PT-UNKNOWN", CancellationToken.None),
            "a logger that throws while reporting the failed refresh must not strand the waiting caller");
        Assert.Null(unknown);
    }

    /// <summary>
    /// A refresh aborted by Dispose() is shutdown, not a failure: it must NOT be logged as the #371
    /// warning signature (a warning carrying a TaskCanceledException), which would otherwise fire on
    /// every pod restart and mask a genuine recurrence.
    /// </summary>
    [Fact]
    public async Task Dispose_DuringABackgroundRefresh_LogsShutdown_NotAFailureWarning()
    {
        var source = new GatedDataSource(Meta("PT1", name: "v1"));
        var logger = new RecordingLogger();
        var cache = Build(source, ttl: TimeSpan.FromMilliseconds(50), missInterval: TimeSpan.FromMinutes(5),
            logger: logger);

        Assert.Equal("v1", (await cache.GetAsync("PT1"))!.Name);

        source.Block();
        await Task.Delay(120); // TTL expired → the next read serves stale and revalidates in background

        Assert.NotNull(await cache.GetAsync("PT1", CancellationToken.None));
        await WithTimeoutAsync(source.Entered, "the background revalidation should have been kicked off");

        cache.Dispose(); // the host is shutting down while the revalidation is in flight
        source.Open();

        await WaitForAsync(
            () => logger.Entries.Any(e => e.Message.Contains("cancelled", StringComparison.Ordinal)),
            "a refresh aborted by Dispose() should log its own shutdown line");

        Assert.DoesNotContain(
            logger.Entries,
            e => e.Level >= LogLevel.Warning && e.Exception is OperationCanceledException);
    }

    /// <summary>E: load failure with a cold cache throws (regression guard).</summary>
    [Fact]
    public async Task GetAsync_LoadFailure_WithColdCache_Throws()
    {
        var source = new GatedDataSource(Meta("PT1"));
        source.FailAlways();
        var cache = Build(source);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetAsync("PT1", CancellationToken.None));
    }
}
