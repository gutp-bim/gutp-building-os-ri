using System.Globalization;
using System.Text.Json;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// <c>telemetry-latest</c> KV を watch して <see cref="PointLastSeenIndexStore"/> に流し込むだけの
/// 薄いアダプタ（#452）。**判断はここに置かない** — 状態遷移も欠測判定も store 側にあり、
/// このクラスは「watch イベント → Apply / Remove」「初期リプレイ完了 → MarkReady」
/// 「切断 → MarkDegraded して張り直し」の対応付けしか持たない（だから単体テストの対象は store）。
///
/// <para>初期リプレイの完了は <c>NatsKVEntry.Delta == 0</c>（残り 0 件）で判る。空バケットは watch が
/// そもそも 1 件も流さないので <c>Delta == 0</c> が来ない。そこだけ <c>GetStatusAsync</c> のメッセージ数で
/// 判断するが、**即 Ready にはしない** — 猶予を置き、明けにもう一度件数を読んで 0 のままのときだけ
/// Ready にする（<see cref="MarkReadyIfStillEmptyAsync"/>）。</para>
///
/// <para>例外・切断では <c>MarkDegraded()</c> して再試行する。**プロセスは落とさない** — 健全性画面が
/// 見られなくなるだけで、テレメトリの取り込みや制御には影響しないため。</para>
/// </summary>
public sealed class NatsKvPointLastSeenIndexWorker : BackgroundService
{
    private const string BucketName = "telemetry-latest";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>空バケットを Ready と決めるまでの既定の猶予（watch が張られて既存値が流れ切るのを待つ）。</summary>
    internal static readonly TimeSpan DefaultEmptyBucketReadyGrace = TimeSpan.FromSeconds(2);

    private readonly INatsJSContext _js;
    private readonly PointLastSeenIndexStore _store;
    private readonly ILogger<NatsKvPointLastSeenIndexWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _emptyBucketReadyGrace;

    public NatsKvPointLastSeenIndexWorker(
        INatsJSContext js,
        PointLastSeenIndexStore store,
        ILogger<NatsKvPointLastSeenIndexWorker> logger,
        TimeProvider? clock = null,
        TimeSpan? emptyBucketReadyGrace = null)
    {
        _js = js;
        _store = store;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _emptyBucketReadyGrace = emptyBucketReadyGrace is { } g && g > TimeSpan.Zero
            ? g
            : DefaultEmptyBucketReadyGrace;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WatchAsync(stoppingToken).ConfigureAwait(false);
                // watch が正常終了した＝購読が閉じた。張り直すまでは追随できていない。
                _store.MarkDegraded();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _store.MarkDegraded();
                _logger.LogWarning(ex,
                    "Point last-seen index watch failed; retrying in {Delay}s", (int)ReconnectDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        var context = new NatsKVContext(_js);
        var kv = await context.CreateStoreAsync(new NatsKVConfig(BucketName) { History = 1 }, ct)
            .ConfigureAwait(false);

        // 張り直しのあいだは「まだ読み切っていない」に戻す(エントリ無し＝欠測と断定させない)。
        _store.MarkWarming();

        // 空バケットは watch が 1 件も流さないので Delta == 0 が来ない。別経路で Ready にする必要がある。
        var status = await kv.GetStatusAsync(ct).ConfigureAwait(false);
        var emptyBucketReady = status.Info.State.Messages == 0
            ? MarkReadyIfStillEmptyAsync(
                async token => (long)(await kv.GetStatusAsync(token).ConfigureAwait(false)).Info.State.Messages,
                ct)
            : Task.CompletedTask;

        try
        {
            await foreach (var entry in kv.WatchAsync<byte[]>(cancellationToken: ct).ConfigureAwait(false))
            {
                var observedAt = _clock.GetUtcNow();

                if (entry.Operation is NatsKVOperation.Del or NatsKVOperation.Purge)
                {
                    _store.Remove(entry.Key, observedAt);
                }
                else if (TryMap(entry, out var mapped))
                {
                    _store.Apply(mapped, observedAt);
                }

                // 初期リプレイの残りが 0 になった時点で「エントリが無い Point は本当に来ていない」と言える。
                if (entry.Delta == 0) _store.MarkReady(observedAt);
            }
        }
        finally
        {
            // 例外を未観測にしない。猶予待ちは ct で畳まれる。
            try { await emptyBucketReady.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 停止中 */ }
        }
    }

    /// <summary>
    /// 「バケットが空だったので即 Ready」を**猶予を置いてから**行う。
    ///
    /// <para>状態を読んだ時点と watch が張り終わる時点のあいだに書き込みが入ると、その瞬間だけ
    /// 「Ready なのに空」になり、全 Point が <c>Missing / NeverReceived</c> と確定表示される
    /// （<c>dataComplete=true</c> なので画面は暫定バナーすら出さない）。それは本設計が防ぎたい
    /// 誤判定そのものなので、watch が張られて既存値が流れ切るだけの猶予を置く。</para>
    ///
    /// <para>猶予中に 1 件でも流れれば <c>Delta == 0</c> 側が先に Ready にするので、ここは
    /// <see cref="PointLastSeenIndexState.Warming"/> のままのときだけ効く。</para>
    ///
    /// <para><b>猶予明けにバケットの件数を読み直す。</b>「まだ Warming」だけでは足りない —
    /// 猶予中に書き込みが入り、その watch 配信がまだ届いていない状態も Warming のままだからだ。
    /// そこで Ready にすると、index は空なのに <c>dataComplete=true</c> で全 Point が
    /// <c>Missing / NeverReceived</c> と確定表示される。件数が 0 でなくなっていたら Ready にせず、
    /// 配信された <c>Delta == 0</c> に判断を譲る（#460 レビュー）。</para>
    /// </summary>
    /// <param name="readMessageCount">バケットの現在のメッセージ数を読む。テストで差し替える。</param>
    /// <param name="ct">停止トークン。</param>
    internal async Task MarkReadyIfStillEmptyAsync(
        Func<CancellationToken, Task<long>> readMessageCount, CancellationToken ct)
    {
        await Task.Delay(_emptyBucketReadyGrace, ct).ConfigureAwait(false);
        if (_store.State != PointLastSeenIndexState.Warming) return;

        long messages;
        try
        {
            messages = await readMessageCount(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 読み直せないなら Ready にしない。Warming のままなら「判定できない」と出るだけで、
            // 誤って欠測を確定表示するより安全。watch 側が動き出せばそちらが Ready にする。
            _logger.LogWarning(ex,
                "Point last-seen index: could not re-check bucket {Bucket} after the ready grace; staying warm",
                BucketName);
            return;
        }

        if (messages != 0)
        {
            _logger.LogInformation(
                "Point last-seen index: bucket {Bucket} received {Messages} message(s) during the ready grace; "
                + "waiting for the watch replay instead",
                BucketName, messages);
            return;
        }

        _store.MarkReady(_clock.GetUtcNow());
        _logger.LogInformation("Point last-seen index: bucket {Bucket} is empty, marked ready", BucketName);
    }

    /// <summary>
    /// KV エントリを index の 1 件へ写す。<b>pointId の正本は値 JSON の <c>PointId</c></b>：KV キーは
    /// <c>NatsKvLatestStore.SanitizeKey</c> による非可逆変換なので、そこから pointId は復元できない。
    /// 値に pointId が無い（古いエントリ）ときだけ KV キーで代用する。
    /// </summary>
    private bool TryMap(NatsKVEntry<byte[]> entry, out PointLastSeenEntry mapped)
    {
        ValidTelemetryData? data = null;
        if (entry.Value is { Length: > 0 } bytes)
        {
            try
            {
                data = JsonSerializer.Deserialize<ValidTelemetryData>(bytes);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Point last-seen index: unreadable value for key {Key}", entry.Key);
            }
        }

        var pointId = string.IsNullOrEmpty(data?.PointId) ? entry.Key : data.PointId;
        if (string.IsNullOrEmpty(pointId))
        {
            mapped = null!;
            return false;
        }

        mapped = new PointLastSeenEntry(pointId, ParseTimestamp(data?.Datetime), data?.Value, data?.ValueType);
        return true;
    }

    /// <summary>
    /// <c>ValidTelemetryData.Datetime</c> は文字列なので、読めなければ <c>null</c>（＝「エントリはあるが
    /// 最終受信時刻が不明」）にする。エントリごと捨てると「一度も来ていない」と区別できなくなる。
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string? datetime)
    {
        if (string.IsNullOrWhiteSpace(datetime)) return null;
        return DateTimeOffset.TryParse(
            datetime, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
