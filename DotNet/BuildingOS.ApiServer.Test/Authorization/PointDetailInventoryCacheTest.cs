using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;

namespace BuildingOS.ApiServer.Test.Authorization;

/// <summary>
/// 建物ごとの Point 台帳キャッシュ（#452）。データ健全性一覧は 1 リクエストで建物全件の台帳を読むので、
/// 画面のポーリングがそのまま OxiGraph への全件 SPARQL になるのを防ぐ層。
///
/// <para>ここで守りたい性質は 3 つある。TTL 内は読み直さないこと、同一建物の同時アクセスを 1 本に
/// まとめること、そして<b>建物 dtId は呼び出し元がクエリで自由に指定できる</b>のでエントリ数に上限が
/// あること。最後の 1 つが無いと、実在しない ID を並べるだけでキーが無制限に増える。</para>
/// </summary>
public class PointDetailInventoryCacheTest
{
    /// <summary>テスト用の手動時計。TTL の経過を実時間を待たずに再現する。</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static FakeClock NewClock() => new(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

    private static PointDetail[] Inventory(params string[] pointIds) =>
        pointIds.Select(id => new PointDetail
        {
            Point = new Point { DtId = $"urn:test:pt:{id}", Id = id, Name = id },
        }).ToArray();

    [Fact]
    public async Task GetAsync_WithinTtl_DoesNotReload()
    {
        var clock = NewClock();
        var cache = new PointDetailInventoryCache(TimeSpan.FromSeconds(60), clock);
        var calls = 0;
        Task<PointDetail[]> Load(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Inventory("PT001"));
        }

        await cache.GetAsync("urn:test:b1", Load, default);
        clock.Advance(TimeSpan.FromSeconds(59));
        var second = await cache.GetAsync("urn:test:b1", Load, default);

        Assert.Equal(1, calls);
        Assert.Single(second);
    }

    [Fact]
    public async Task GetAsync_AfterTtl_Reloads()
    {
        var clock = NewClock();
        var cache = new PointDetailInventoryCache(TimeSpan.FromSeconds(60), clock);
        var calls = 0;
        Task<PointDetail[]> Load(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Inventory("PT001"));
        }

        await cache.GetAsync("urn:test:b1", Load, default);
        clock.Advance(TimeSpan.FromSeconds(61));
        await cache.GetAsync("urn:test:b1", Load, default);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetAsync_SeparateBuildings_AreCachedIndependently()
    {
        var cache = new PointDetailInventoryCache(TimeSpan.FromSeconds(60), NewClock());

        var a = await cache.GetAsync("urn:test:b1", _ => Task.FromResult(Inventory("PT001")), default);
        var b = await cache.GetAsync("urn:test:b2", _ => Task.FromResult(Inventory("PT002", "PT003")), default);

        Assert.Single(a);
        Assert.Equal(2, b.Length);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task GetAsync_ConcurrentSameBuilding_LoadsOnce()
    {
        var cache = new PointDetailInventoryCache(TimeSpan.FromSeconds(60), NewClock());
        var gate = new TaskCompletionSource();
        var calls = 0;
        async Task<PointDetail[]> Load(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task.ConfigureAwait(false);
            return Inventory("PT001");
        }

        var first = cache.GetAsync("urn:test:b1", Load, default);
        var second = cache.GetAsync("urn:test:b1", Load, default);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
    }

    /// <summary>
    /// 相乗りしたロードが「先行リクエストの離脱」で落ちても、生きている呼び出し元まで巻き添えにしない。
    /// 共有ロードは先行リクエストの CancellationToken で走るので、これが無いと画面のポーリング中に
    /// 別のリクエストが 500 になる。
    /// </summary>
    [Fact]
    public async Task GetAsync_SharedLoadCancelled_ButCallerAlive_ReloadsInsteadOfThrowing()
    {
        var cache = new PointDetailInventoryCache(TimeSpan.FromSeconds(60), NewClock());
        var calls = 0;
        Task<PointDetail[]> Load(CancellationToken _)
        {
            if (Interlocked.Increment(ref calls) == 1) throw new OperationCanceledException();
            return Task.FromResult(Inventory("PT001"));
        }

        var result = await cache.GetAsync("urn:test:b1", Load, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, calls);
    }

    /// <summary>呼び出し元自身が中断していれば、素直にキャンセルを伝播する（無限に読み直さない）。</summary>
    [Fact]
    public async Task GetAsync_CallerCancelled_Propagates()
    {
        var cache = new PointDetailInventoryCache(TimeSpan.FromSeconds(60), NewClock());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cache.GetAsync("urn:test:b1", ct => Task.FromException<PointDetail[]>(new OperationCanceledException(ct)), cts.Token));
    }

    /// <summary>
    /// 建物 dtId は呼び出し元が自由に指定できるので、実在しない ID を並べられてもエントリ数は上限で頭打ちになる。
    /// </summary>
    [Fact]
    public async Task GetAsync_ManyDistinctBuildings_IsBounded()
    {
        var cache = new PointDetailInventoryCache(TimeSpan.FromMinutes(10), NewClock());

        for (var i = 0; i < PointDetailInventoryCache.MaxEntries + 50; i++)
            await cache.GetAsync($"urn:test:b{i}", _ => Task.FromResult(Inventory($"PT{i}")), default);

        Assert.True(
            cache.Count <= PointDetailInventoryCache.MaxEntries,
            $"エントリ数が上限を超えている: {cache.Count} > {PointDetailInventoryCache.MaxEntries}");
    }
}
