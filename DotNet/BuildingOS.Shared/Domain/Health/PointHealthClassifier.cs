using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Domain.Types;

namespace BuildingOS.Shared.Domain.Health;

/// <summary>
/// データ健全性の 3 軸判定（#452）。**副作用も I/O も持たない純関数の集まり**で、台帳にも index にも
/// 触らない。呼び出し元が <see cref="PointHealthInput"/> に材料を集めてから渡す。
///
/// <para><b>フロントの既存純関数と意味論を一致させること</b>が仕様。境界は
/// <c>web-client/src/lib/telemetry/freshness.ts</c> / <c>alarm.ts</c> / <c>freshness-threshold.ts</c>
/// と同じで、片方だけ変えると画面とサーバの判定が食い違う。</para>
///
/// <para>3 軸（鮮度 / 警報 / gateway 接続）は独立で、1 つの Point が <c>Fresh</c> かつ
/// <c>Critical</c> になり得る。<see cref="HealthStatus"/> はその派生であって軸の代替ではない。</para>
/// </summary>
public static class PointHealthClassifier
{
    /// <summary>
    /// 期待間隔に掛ける既定倍率。<c>SettingsRegistry</c> の <c>telemetry.staleIntervalMultiplier</c>
    /// の既定値と同じ値で、設定値が使えない（非有限 / 非正）ときの戻り先でもある。
    /// </summary>
    public const double DefaultStaleIntervalMultiplier = 3;

    /// <summary>
    /// 期待間隔が無いときに使う既定閾値（秒）。設定値が壊れている場合の戻り先。
    /// <c>SettingsRegistry</c> の <c>telemetry.staleThresholdSeconds</c> の既定値と同じ。
    /// </summary>
    public const double DefaultStaleThresholdSeconds = 300;

    /// <summary>
    /// 鮮度閾値を解決する。期待間隔は Point → Device → Gateway の順で**最初の有限かつ正**の値を採り、
    /// それに倍率を掛ける。どの階層にも無ければ system 既定値を**そのまま**使う
    /// （掛ける対象が無いので倍率は適用しない）。
    /// </summary>
    public static ThresholdResolution ResolveThreshold(ExpectedIntervals expected, TelemetryThresholds defaults)
    {
        var multiplier = IsUsable(defaults.StaleIntervalMultiplier)
            ? defaults.StaleIntervalMultiplier
            : DefaultStaleIntervalMultiplier;

        foreach (var (interval, source) in new[]
                 {
                     (expected.Point, ThresholdSource.Point),
                     (expected.Device, ThresholdSource.Device),
                     (expected.Gateway, ThresholdSource.Gateway),
                 })
        {
            if (interval is { } value && IsUsable(value))
            {
                return new ThresholdResolution
                {
                    ThresholdSeconds = value * multiplier,
                    ExpectedIntervalSeconds = value,
                    Source = source,
                };
            }
        }

        return new ThresholdResolution
        {
            ThresholdSeconds = IsUsable(defaults.StaleThresholdSeconds)
                ? defaults.StaleThresholdSeconds
                : DefaultStaleThresholdSeconds,
            ExpectedIntervalSeconds = null,
            Source = ThresholdSource.System,
        };
    }

    /// <summary>
    /// 鮮度を判定する。<paramref name="indexReady"/> が false のあいだは
    /// **エントリが無い Point を <see cref="FreshnessStatus.Missing"/> にしない**（起動直後に
    /// index が空なだけで「全 Point 欠測」と報告しないための、この機能の要）。
    /// </summary>
    public static PointFreshnessResult ClassifyFreshness(
        PointHealthInput input, TelemetryThresholds defaults, bool indexReady, DateTimeOffset now)
    {
        var threshold = ResolveThreshold(input.Expected, defaults);

        if (input.LastSeen is not { } lastSeen)
        {
            // 判定材料が無い（index が温まっておらず、この Point のエントリも無い）＝判定不能。
            var status = !input.HasIndexEntry && !indexReady ? FreshnessStatus.Unknown : FreshnessStatus.Missing;
            return new PointFreshnessResult
            {
                Status = status,
                LastSeen = null,
                AgeSeconds = null,
                ExpectedIntervalSeconds = threshold.ExpectedIntervalSeconds,
                ThresholdSeconds = threshold.ThresholdSeconds,
                ThresholdSource = threshold.Source,
                Reason = status == FreshnessStatus.Missing ? ResolveMissingReason(input) : null,
            };
        }

        // クロックずれで未来の timestamp が来ても Stale にはせず 0 で下限クリップ。
        var elapsed = (now - lastSeen).TotalSeconds;
        var elapsedClamped = elapsed <= 0 ? 0d : elapsed;
        // 表示用の齢だけ秒に floor する。**比較は floor 前の値で行う** —
        // 先に丸めると閾値 300 秒に対して 300.7 秒経過が Fresh になり、ミリ秒で比較する
        // フロント（freshness.ts の classifyPointFreshness）と同じ Point が逆の判定になる。
        // 期待周期由来の閾値は小数になり得る（2.5 秒 × 3 = 7.5 秒）ので、なおさら丸めてはいけない。
        var age = (long)Math.Floor(elapsedClamped);

        return new PointFreshnessResult
        {
            Status = elapsedClamped > threshold.ThresholdSeconds ? FreshnessStatus.Stale : FreshnessStatus.Fresh,
            LastSeen = lastSeen,
            AgeSeconds = age,
            ExpectedIntervalSeconds = threshold.ExpectedIntervalSeconds,
            ThresholdSeconds = threshold.ThresholdSeconds,
            ThresholdSource = threshold.Source,
            Reason = null,
        };
    }

    /// <summary>
    /// 警報を判定する。<paramref name="freshness"/> が <see cref="FreshnessStatus.Fresh"/> でなければ
    /// **抑制**する（届いていない値で異常判定をすると、gateway 断のたびに警報件数が跳ね上がる）。
    /// 閾値が 1 つも無い、または値が無い場合は <see cref="AlarmStatus.Unknown"/>。
    /// </summary>
    public static PointAlarmResult ClassifyAlarm(PointHealthInput input, FreshnessStatus freshness)
    {
        // 抑制時も値そのものは表示に使うので残す。
        if (freshness != FreshnessStatus.Fresh)
            return new PointAlarmResult { Status = AlarmStatus.Suppressed, Value = input.Value, Violated = null };

        var t = input.Thresholds;
        var hasThreshold = t.AlarmHigh is not null || t.AlarmLow is not null
                                                   || t.WarnHigh is not null || t.WarnLow is not null;
        if (!hasThreshold || input.Value is not { } value)
            return new PointAlarmResult { Status = AlarmStatus.Unknown, Value = input.Value, Violated = null };

        // 境界は inclusive（到達で違反）。critical が warn より優先。high / low は独立。
        if (t.AlarmHigh is { } alarmHigh && value >= alarmHigh)
            return new PointAlarmResult { Status = AlarmStatus.Critical, Value = value, Violated = AlarmBound.AlarmHigh };
        if (t.AlarmLow is { } alarmLow && value <= alarmLow)
            return new PointAlarmResult { Status = AlarmStatus.Critical, Value = value, Violated = AlarmBound.AlarmLow };
        if (t.WarnHigh is { } warnHigh && value >= warnHigh)
            return new PointAlarmResult { Status = AlarmStatus.Warn, Value = value, Violated = AlarmBound.WarnHigh };
        if (t.WarnLow is { } warnLow && value <= warnLow)
            return new PointAlarmResult { Status = AlarmStatus.Warn, Value = value, Violated = AlarmBound.WarnLow };

        return new PointAlarmResult { Status = AlarmStatus.Normal, Value = value, Violated = null };
    }

    /// <summary>
    /// 総合ステータスを導く。警報が鮮度より優先（Fresh でも Critical なら Critical）で、
    /// それ以外は鮮度をそのまま写す。
    /// </summary>
    public static HealthStatus DeriveHealthStatus(FreshnessStatus freshness, AlarmStatus alarm) => alarm switch
    {
        AlarmStatus.Critical => HealthStatus.Critical,
        AlarmStatus.Warn => HealthStatus.Warn,
        _ => freshness switch
        {
            FreshnessStatus.Stale => HealthStatus.Stale,
            FreshnessStatus.Missing => HealthStatus.Missing,
            FreshnessStatus.Unknown => HealthStatus.Unknown,
            _ => HealthStatus.Fresh,
        },
    };

    /// <summary>3 軸をまとめて判定する。</summary>
    public static PointHealthResult Classify(
        PointHealthInput input, TelemetryThresholds defaults, bool indexReady, DateTimeOffset now)
    {
        var freshness = ClassifyFreshness(input, defaults, indexReady, now);
        var alarm = ClassifyAlarm(input, freshness.Status);
        return new PointHealthResult
        {
            Freshness = freshness,
            Alarm = alarm,
            HealthStatus = DeriveHealthStatus(freshness.Status, alarm.Status),
        };
    }

    /// <summary>
    /// 欠測の理由。**gateway 断が「一度も来ていない」より優先**（運用者の次の一手に直結するため）。
    /// index にエントリがあるのに時刻が無い＝timestamp が読めなかったので Unknown。
    ///
    /// <para>ただし優先されるのは <see cref="GatewayConnectionState.Disconnected"/>（観測上つながって
    /// いない）だけで、<see cref="GatewayConnectionState.Unknown"/>（接続状態を読めなかった）では
    /// gateway 断を名乗らない（#463）。優先順位が高いぶん、ここで丸めると KV の不調のたびに欠測
    /// Point が全件 gateway 断に見える。index 由来の理由は**自分のインデックスについての事実**なので、
    /// gateway の状態が分からなくても成り立つ。</para>
    /// </summary>
    private static MissingReason ResolveMissingReason(PointHealthInput input)
    {
        if (!string.IsNullOrEmpty(input.GatewayId)
            && input.GatewayState == GatewayConnectionState.Disconnected)
            return MissingReason.GatewayDisconnected;
        return !input.HasIndexEntry ? MissingReason.NeverReceived : MissingReason.Unknown;
    }

    /// <summary>0 秒間隔（全件欠測になる）や NaN / ∞ は採用しない。</summary>
    private static bool IsUsable(double value) => double.IsFinite(value) && value > 0;
}
