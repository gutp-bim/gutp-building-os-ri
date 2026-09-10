using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Domain.Health;

namespace BuildingOS.Shared.Test.Domain.Health;

/// <summary>
/// データ健全性分類器（#452）の仕様。
///
/// <para><b>フロント既存純関数との意味論一致が仕様</b>。境界ケースは
/// <c>web-client/src/lib/telemetry/freshness.ts</c> / <c>alarm.ts</c> / <c>freshness-threshold.ts</c>
/// とそれぞれの <c>*.test.ts</c> から移植している。片方だけ直すと画面とサーバで判定が食い違うので、
/// 仕様を変えるときは必ず両方を同時に直すこと。</para>
///
/// <para>3 軸（freshness / alarm / gateway 接続）は独立で、1 つの Point が
/// <c>freshness=Fresh</c> かつ <c>alarm=Critical</c> になり得る（ADR-0004 / ADR-0005）。
/// <see cref="HealthStatus"/> はその派生値であって、軸を潰した代替ではない。</para>
/// </summary>
public class PointHealthClassifierTest
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>システム既定（telemetry.staleThresholdSeconds=300 / staleIntervalMultiplier=3）。</summary>
    private static readonly TelemetryThresholds Defaults = new(300, 3);

    /// <summary>Now から <paramref name="seconds"/> 秒前。負値なら未来（クロックずれ）。</summary>
    private static DateTimeOffset Ago(double seconds) => Now.AddSeconds(-seconds);

    private static PointHealthInput Input(
        DateTimeOffset? lastSeen,
        bool hasIndexEntry = true,
        double? value = null,
        ExpectedIntervals? expected = null,
        PointAlarmThresholds? thresholds = null,
        string? gatewayId = null,
        bool gatewayConnected = true) => new()
        {
            PointId = "PT001",
            LastSeen = lastSeen,
            HasIndexEntry = hasIndexEntry,
            Value = value,
            Expected = expected ?? new ExpectedIntervals(),
            Thresholds = thresholds ?? new PointAlarmThresholds(),
            GatewayId = gatewayId,
            GatewayConnected = gatewayConnected,
        };

    // ---------------------------------------------------------------------
    // freshness の境界（freshness.test.ts からの移植）
    // ---------------------------------------------------------------------

    [Fact]
    public void ClassifyFreshness_NoLastSeen_IsMissingWithNullAge()
    {
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(lastSeen: null, hasIndexEntry: true), Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Missing, r.Status);
        Assert.Null(r.LastSeen);
        Assert.Null(r.AgeSeconds);
    }

    [Fact]
    public void ClassifyFreshness_JustReceived_IsFreshWithAgeZero()
    {
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(0)), Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Fresh, r.Status);
        Assert.Equal(0, r.AgeSeconds);
    }

    [Fact]
    public void ClassifyFreshness_ExactlyAtThreshold_IsFresh()
    {
        // 閾値ちょうどは Fresh（境界は inclusive）。
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(300)), Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Fresh, r.Status);
        Assert.Equal(300, r.AgeSeconds);
    }

    [Fact]
    public void ClassifyFreshness_OneSecondPastThreshold_IsStale()
    {
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(301)), Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Stale, r.Status);
        Assert.Equal(301, r.AgeSeconds);
    }

    [Fact]
    public void ClassifyFreshness_SubSecondRemainder_FloorsAgeToWholeSeconds()
    {
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(Now.AddMilliseconds(-42_800)), Defaults, indexReady: true, Now);

        Assert.Equal(42, r.AgeSeconds);
    }

    /// <summary>
    /// 表示用の齢は秒に floor するが、**比較は floor 前の値**で行う。先に丸めると閾値 300 秒に対して
    /// 300.7 秒経過が Fresh になり、ミリ秒で比較するフロント（<c>freshness.ts</c>）と同じ Point が
    /// 逆の判定になる。「/home では鮮度切れ、/health では正常」はこの 1 行から生まれる。
    /// </summary>
    [Fact]
    public void ClassifyFreshness_FractionalSecondPastThreshold_IsStale()
    {
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(Now.AddMilliseconds(-300_700)), Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Stale, r.Status);
        Assert.Equal(300, r.AgeSeconds); // 表示は floor したまま
    }

    /// <summary>
    /// 期待周期由来の閾値は小数になり得る（2.5 秒 × 3 = 7.5 秒）。齢を丸めてから比べると
    /// 7.9 秒経過が「7 &gt; 7.5 は偽」で Fresh になってしまう。
    /// </summary>
    [Fact]
    public void ClassifyFreshness_FractionalThreshold_ComparesWithoutRounding()
    {
        var input = Input(Now.AddMilliseconds(-7_900), expected: new ExpectedIntervals(Point: 2.5));

        var r = PointHealthClassifier.ClassifyFreshness(input, Defaults, indexReady: true, Now);

        Assert.Equal(7.5, r.ThresholdSeconds);
        Assert.Equal(FreshnessStatus.Stale, r.Status);
    }

    [Fact]
    public void ClassifyFreshness_FutureTimestamp_IsFreshWithAgeClippedToZero()
    {
        // クロックずれで未来の timestamp が来ても Stale にはせず、age は 0 で下限クリップする。
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(-30)), Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Fresh, r.Status);
        Assert.Equal(0, r.AgeSeconds);
    }

    [Fact]
    public void ClassifyFreshness_PerPointThreshold_OverridesSystemDefault()
    {
        // 速い点（5s → 閾値 15s）は 120s で Stale、遅い点（1 日）は同じ齢でも Fresh。
        var fast = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(120), expected: new ExpectedIntervals(Point: 5)), Defaults, indexReady: true, Now);
        var slow = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(120), expected: new ExpectedIntervals(Point: 86_400)), Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Stale, fast.Status);
        Assert.Equal(FreshnessStatus.Fresh, slow.Status);
    }

    // ---------------------------------------------------------------------
    // 閾値解決（freshness-threshold.test.ts からの移植）
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveThreshold_PointInterval_MultipliesAndReportsPointSource()
    {
        var r = PointHealthClassifier.ResolveThreshold(new ExpectedIntervals(Point: 60), Defaults);

        Assert.Equal(180, r.ThresholdSeconds);
        Assert.Equal(60, r.ExpectedIntervalSeconds);
        Assert.Equal(ThresholdSource.Point, r.Source);
    }

    [Fact]
    public void ResolveThreshold_PrefersPointOverDeviceAndGateway()
    {
        var r = PointHealthClassifier.ResolveThreshold(
            new ExpectedIntervals(Point: 60, Device: 120, Gateway: 300), Defaults);

        Assert.Equal(60, r.ExpectedIntervalSeconds);
        Assert.Equal(ThresholdSource.Point, r.Source);
    }

    [Fact]
    public void ResolveThreshold_FallsBackToDeviceThenGateway()
    {
        var device = PointHealthClassifier.ResolveThreshold(
            new ExpectedIntervals(Device: 600, Gateway: 300), Defaults);
        Assert.Equal(1800, device.ThresholdSeconds);
        Assert.Equal(ThresholdSource.Device, device.Source);

        var gateway = PointHealthClassifier.ResolveThreshold(
            new ExpectedIntervals(Gateway: 300), Defaults);
        Assert.Equal(900, gateway.ThresholdSeconds);
        Assert.Equal(ThresholdSource.Gateway, gateway.Source);
    }

    [Fact]
    public void ResolveThreshold_NoTier_UsesSystemDefaultWithoutMultiplier()
    {
        // 期待間隔が無いときは「掛けるものが無い」ので system default をそのまま使う（×3 しない）。
        var r = PointHealthClassifier.ResolveThreshold(new ExpectedIntervals(), Defaults);

        Assert.Equal(300, r.ThresholdSeconds);
        Assert.Null(r.ExpectedIntervalSeconds);
        Assert.Equal(ThresholdSource.System, r.Source);
    }

    [Theory]
    [InlineData(0d)]     // 0 秒間隔は「全部欠測」になるので採用しない
    [InlineData(-5d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ResolveThreshold_UnusableIntervalAtATier_ContinuesDownTheHierarchy(double bad)
    {
        var r = PointHealthClassifier.ResolveThreshold(
            new ExpectedIntervals(Point: bad, Device: 90), Defaults);

        Assert.Equal(270, r.ThresholdSeconds);
        Assert.Equal(90, r.ExpectedIntervalSeconds);
        Assert.Equal(ThresholdSource.Device, r.Source);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ResolveThreshold_UnusableMultiplier_RevertsToDefaultThree(double multiplier)
    {
        var r = PointHealthClassifier.ResolveThreshold(
            new ExpectedIntervals(Point: 60), new TelemetryThresholds(300, multiplier));

        Assert.Equal(60 * PointHealthClassifier.DefaultStaleIntervalMultiplier, r.ThresholdSeconds);
    }

    [Fact]
    public void DefaultStaleIntervalMultiplier_MirrorsRegistryDefault()
    {
        Assert.Equal(3d, PointHealthClassifier.DefaultStaleIntervalMultiplier);
    }

    [Fact]
    public void ClassifyFreshness_ReportsResolvedThresholdAndSource()
    {
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(10), expected: new ExpectedIntervals(Point: 300)), Defaults, indexReady: true, Now);

        Assert.Equal(300, r.ExpectedIntervalSeconds);
        Assert.Equal(900, r.ThresholdSeconds);
        Assert.Equal(ThresholdSource.Point, r.ThresholdSource);
    }

    // ---------------------------------------------------------------------
    // index が温まっていないとき（false missing を出さない）
    // ---------------------------------------------------------------------

    [Fact]
    public void ClassifyFreshness_IndexNotReadyAndNoEntry_IsUnknownNotMissing()
    {
        // 起動直後に index が空なだけで「全 Point 欠測」と報告してはならない。判定不能は Unknown。
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(lastSeen: null, hasIndexEntry: false), Defaults, indexReady: false, Now);

        Assert.Equal(FreshnessStatus.Unknown, r.Status);
        Assert.Null(r.AgeSeconds);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void ClassifyFreshness_IndexNotReadyButEntryExists_IsClassifiedNormally()
    {
        var stale = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(1000), hasIndexEntry: true), Defaults, indexReady: false, Now);
        Assert.Equal(FreshnessStatus.Stale, stale.Status);

        var fresh = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(10), hasIndexEntry: true), Defaults, indexReady: false, Now);
        Assert.Equal(FreshnessStatus.Fresh, fresh.Status);
    }

    [Fact]
    public void ClassifyFreshness_IndexReadyAndNoEntry_IsMissing()
    {
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(lastSeen: null, hasIndexEntry: false), Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Missing, r.Status);
    }

    // ---------------------------------------------------------------------
    // missing の理由
    // ---------------------------------------------------------------------

    [Fact]
    public void ClassifyFreshness_MissingWithNoIndexEntry_ReasonIsNeverReceived()
    {
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(lastSeen: null, hasIndexEntry: false), Defaults, indexReady: true, Now);

        Assert.Equal(MissingReason.NeverReceived, r.Reason);
    }

    [Fact]
    public void ClassifyFreshness_MissingWithDisconnectedGateway_ReasonIsGatewayDisconnected()
    {
        // gateway が落ちている方が「一度も来ていない」より説明力が高いので優先する。
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(lastSeen: null, hasIndexEntry: false, gatewayId: "GW-001", gatewayConnected: false),
            Defaults, indexReady: true, Now);

        Assert.Equal(MissingReason.GatewayDisconnected, r.Reason);
    }

    [Fact]
    public void ClassifyFreshness_MissingWithEntryButUnparseableTimestamp_ReasonIsUnknown()
    {
        // index にエントリはあるが timestamp が読めなかった（LastSeen=null）ケース。
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(lastSeen: null, hasIndexEntry: true, gatewayId: "GW-001", gatewayConnected: true),
            Defaults, indexReady: true, Now);

        Assert.Equal(MissingReason.Unknown, r.Reason);
    }

    [Fact]
    public void ClassifyFreshness_NotMissing_HasNoReason()
    {
        var r = PointHealthClassifier.ClassifyFreshness(
            Input(Ago(1000), gatewayId: "GW-001", gatewayConnected: false), Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Stale, r.Status);
        Assert.Null(r.Reason);
    }

    // ---------------------------------------------------------------------
    // alarm（alarm.ts からの移植）
    // ---------------------------------------------------------------------

    [Fact]
    public void ClassifyAlarm_NoThresholds_IsUnknown()
    {
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: 23.4), FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Unknown, r.Status);
        Assert.Equal(23.4, r.Value);
        Assert.Null(r.Violated);
    }

    [Fact]
    public void ClassifyAlarm_NoValue_IsUnknown()
    {
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: null, thresholds: new PointAlarmThresholds(AlarmHigh: 30)),
            FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Unknown, r.Status);
        Assert.Null(r.Value);
        Assert.Null(r.Violated);
    }

    [Fact]
    public void ClassifyAlarm_WithinThresholds_IsNormal()
    {
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: 22,
                thresholds: new PointAlarmThresholds(AlarmHigh: 30, AlarmLow: 10, WarnHigh: 28, WarnLow: 12)),
            FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Normal, r.Status);
        Assert.Null(r.Violated);
    }

    [Fact]
    public void ClassifyAlarm_AtAlarmHigh_IsCriticalWithAlarmHighViolated()
    {
        // 到達（>=）で critical。境界は inclusive。
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: 30, thresholds: new PointAlarmThresholds(AlarmHigh: 30)),
            FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Critical, r.Status);
        Assert.Equal(AlarmBound.AlarmHigh, r.Violated);
    }

    [Fact]
    public void ClassifyAlarm_AtAlarmLow_IsCriticalWithAlarmLowViolated()
    {
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: 5, thresholds: new PointAlarmThresholds(AlarmLow: 5)),
            FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Critical, r.Status);
        Assert.Equal(AlarmBound.AlarmLow, r.Violated);
    }

    [Fact]
    public void ClassifyAlarm_OnlyWarnHighReached_IsWarn()
    {
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: 28, thresholds: new PointAlarmThresholds(AlarmHigh: 30, WarnHigh: 28)),
            FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Warn, r.Status);
        Assert.Equal(AlarmBound.WarnHigh, r.Violated);
    }

    [Fact]
    public void ClassifyAlarm_OnlyWarnLowReached_IsWarn()
    {
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: 12, thresholds: new PointAlarmThresholds(AlarmLow: 10, WarnLow: 12)),
            FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Warn, r.Status);
        Assert.Equal(AlarmBound.WarnLow, r.Violated);
    }

    [Fact]
    public void ClassifyAlarm_CriticalWinsOverWarn()
    {
        // warn も critical も超えている値は critical。
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: 35, thresholds: new PointAlarmThresholds(AlarmHigh: 30, WarnHigh: 28)),
            FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Critical, r.Status);
        Assert.Equal(AlarmBound.AlarmHigh, r.Violated);
    }

    [Fact]
    public void ClassifyAlarm_HighAndLowAreIndependent()
    {
        var highOnly = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: -50, thresholds: new PointAlarmThresholds(AlarmHigh: 30)),
            FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Normal, highOnly.Status);
    }

    // ---------------------------------------------------------------------
    // suppression（届いていない値で異常判定しない）
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(FreshnessStatus.Stale)]
    [InlineData(FreshnessStatus.Missing)]
    [InlineData(FreshnessStatus.Unknown)]
    public void ClassifyAlarm_NotFresh_IsSuppressedEvenWithValueAndThresholds(FreshnessStatus freshness)
    {
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(9999), value: 99, thresholds: new PointAlarmThresholds(AlarmHigh: 30)),
            freshness);

        Assert.Equal(AlarmStatus.Suppressed, r.Status);
        Assert.Null(r.Violated);
    }

    [Fact]
    public void ClassifyAlarm_Fresh_IsNotSuppressed()
    {
        var r = PointHealthClassifier.ClassifyAlarm(
            Input(Ago(0), value: 99, thresholds: new PointAlarmThresholds(AlarmHigh: 30)),
            FreshnessStatus.Fresh);

        Assert.Equal(AlarmStatus.Critical, r.Status);
    }

    // ---------------------------------------------------------------------
    // healthStatus の派生
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(FreshnessStatus.Fresh, AlarmStatus.Critical, HealthStatus.Critical)]
    [InlineData(FreshnessStatus.Fresh, AlarmStatus.Warn, HealthStatus.Warn)]
    [InlineData(FreshnessStatus.Fresh, AlarmStatus.Normal, HealthStatus.Fresh)]
    [InlineData(FreshnessStatus.Fresh, AlarmStatus.Unknown, HealthStatus.Fresh)]
    [InlineData(FreshnessStatus.Stale, AlarmStatus.Suppressed, HealthStatus.Stale)]
    [InlineData(FreshnessStatus.Missing, AlarmStatus.Suppressed, HealthStatus.Missing)]
    [InlineData(FreshnessStatus.Unknown, AlarmStatus.Suppressed, HealthStatus.Unknown)]
    public void DeriveHealthStatus_AlarmOutranksFreshness(
        FreshnessStatus freshness, AlarmStatus alarm, HealthStatus expected)
    {
        Assert.Equal(expected, PointHealthClassifier.DeriveHealthStatus(freshness, alarm));
    }

    [Fact]
    public void HealthStatus_EnumOrderIsWorstFirst()
    {
        // sort=worst はこの宣言順（= 数値順）をそのまま severity として使う。
        Assert.True((int)HealthStatus.Critical < (int)HealthStatus.Warn);
        Assert.True((int)HealthStatus.Warn < (int)HealthStatus.Missing);
        Assert.True((int)HealthStatus.Missing < (int)HealthStatus.Stale);
        Assert.True((int)HealthStatus.Stale < (int)HealthStatus.Unknown);
        Assert.True((int)HealthStatus.Unknown < (int)HealthStatus.Fresh);
    }

    // ---------------------------------------------------------------------
    // Classify（3 軸まとめ）
    // ---------------------------------------------------------------------

    [Fact]
    public void Classify_FreshAndOverAlarmHigh_KeepsBothAxesIndependent()
    {
        // 1 つの Point が freshness=Fresh かつ alarm=Critical になり得る（軸を潰さない）。
        var r = PointHealthClassifier.Classify(
            Input(Ago(10), value: 35, thresholds: new PointAlarmThresholds(AlarmHigh: 30)),
            Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Fresh, r.Freshness.Status);
        Assert.Equal(AlarmStatus.Critical, r.Alarm.Status);
        Assert.Equal(HealthStatus.Critical, r.HealthStatus);
    }

    [Fact]
    public void Classify_StaleWithBreachingValue_SuppressesAlarmAndReportsStale()
    {
        var r = PointHealthClassifier.Classify(
            Input(Ago(1000), value: 35, thresholds: new PointAlarmThresholds(AlarmHigh: 30)),
            Defaults, indexReady: true, Now);

        Assert.Equal(FreshnessStatus.Stale, r.Freshness.Status);
        Assert.Equal(AlarmStatus.Suppressed, r.Alarm.Status);
        Assert.Equal(HealthStatus.Stale, r.HealthStatus);
    }

    [Fact]
    public void Classify_IndexWarmingAndNoEntry_IsUnknownWithSuppressedAlarm()
    {
        var r = PointHealthClassifier.Classify(
            Input(lastSeen: null, hasIndexEntry: false, value: 35,
                thresholds: new PointAlarmThresholds(AlarmHigh: 30)),
            Defaults, indexReady: false, Now);

        Assert.Equal(FreshnessStatus.Unknown, r.Freshness.Status);
        Assert.Equal(AlarmStatus.Suppressed, r.Alarm.Status);
        Assert.Equal(HealthStatus.Unknown, r.HealthStatus);
    }
}
