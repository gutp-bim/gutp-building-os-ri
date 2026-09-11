using System.Text.Json.Serialization;

namespace BuildingOS.Shared.Domain.Health;

/// <summary>
/// 鮮度（最終受信からの経過）の判定結果（#452）。
/// <c>Unknown</c> は「index が温まっていない＝判定材料が無い」であって、欠測（<c>Missing</c>）ではない。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FreshnessStatus
{
    Fresh,
    Stale,
    Missing,
    Unknown,
}

/// <summary>
/// 値の警報判定（#158 Phase 2a / ADR-0005）。<c>Suppressed</c> は「鮮度が切れているので
/// 届いていない値では警報を出さない」の意で、正常（<c>Normal</c>）とは区別する。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlarmStatus
{
    Normal,
    Warn,
    Critical,
    Unknown,
    Suppressed,
}

/// <summary>
/// 3 軸から導出する 1 本の総合ステータス。**宣言順がそのまま深刻度**（worst-first）で、
/// <c>sort=worst</c> はこの数値順を severity として使う。順番を入れ替えると並びが変わる。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HealthStatus
{
    Critical,
    Warn,
    Missing,
    Stale,
    Unknown,
    Fresh,
}

/// <summary>鮮度閾値をどの階層の期待間隔から導いたか。<c>System</c> は期待間隔が無く既定値を使った場合。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThresholdSource
{
    Point,
    Device,
    Gateway,
    System,
}

/// <summary>欠測の理由。運用者の次の一手（gateway を見るのか台帳を見るのか）を分けるための区別。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MissingReason
{
    NeverReceived,
    GatewayDisconnected,
    Unknown,
}

/// <summary>どの警報境界に触れたか。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlarmBound
{
    AlarmHigh,
    AlarmLow,
    WarnHigh,
    WarnLow,
}

/// <summary>一覧の並べ替え軸。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PointHealthSort
{
    Worst,
    LastSeen,
    Name,
}

/// <summary>
/// 期待受信間隔（秒）の階層。Point → Device → Gateway の順に最初の「有限かつ正」の値を採用する。
/// 現状の台帳が持つのは Point（<c>sbco:interval</c>）だけだが、将来の device/gateway 既定を
/// 呼び出し元が足せるよう 3 段で受ける。
/// </summary>
public sealed record ExpectedIntervals(double? Point = null, double? Device = null, double? Gateway = null);

/// <summary>
/// Point ごとの警報閾値（#158 Phase 2a）。alarm* が critical（外側）、warn* が内側。すべて任意。
/// </summary>
public sealed record PointAlarmThresholds(
    double? AlarmHigh = null,
    double? AlarmLow = null,
    double? WarnHigh = null,
    double? WarnLow = null);

/// <summary>
/// 1 Point ぶんの判定入力。台帳（期待間隔・警報閾値・gateway）と最終受信インデックス
/// （<c>LastSeen</c> / <c>Value</c> / <c>HasIndexEntry</c>）を突き合わせた結果をここに集める。
/// </summary>
public sealed class PointHealthInput
{
    public string PointId { get; set; } = string.Empty;

    /// <summary>最終受信時刻。index にエントリがあっても timestamp が読めなければ null。</summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>index にこの Point のエントリが載っていたか。<c>LastSeen</c> が null のときの理由分けに使う。</summary>
    public bool HasIndexEntry { get; set; }

    public double? Value { get; set; }
    public ExpectedIntervals Expected { get; set; } = new();
    public PointAlarmThresholds Thresholds { get; set; } = new();
    public string? GatewayId { get; set; }
    public bool GatewayConnected { get; set; }
}

/// <summary>鮮度閾値の解決結果（採用した期待間隔・その出所・秒数）。</summary>
public sealed record ThresholdResolution
{
    public double ThresholdSeconds { get; init; }
    public double? ExpectedIntervalSeconds { get; init; }
    public ThresholdSource Source { get; init; }
}

/// <summary>鮮度軸の判定結果。<c>AgeSeconds</c> は 0 下限で秒に floor した値、未受信なら null。</summary>
public sealed record PointFreshnessResult
{
    public FreshnessStatus Status { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public long? AgeSeconds { get; init; }
    public double? ExpectedIntervalSeconds { get; init; }
    public double ThresholdSeconds { get; init; }
    public ThresholdSource ThresholdSource { get; init; }

    /// <summary><c>Missing</c> のときだけ入る理由。それ以外は null。</summary>
    public MissingReason? Reason { get; init; }
}

/// <summary>警報軸の判定結果。<c>Value</c> は抑制時も表示のために残す。</summary>
public sealed record PointAlarmResult
{
    public AlarmStatus Status { get; init; }
    public double? Value { get; init; }
    public AlarmBound? Violated { get; init; }
}

/// <summary>3 軸の判定結果と、そこから導いた総合ステータス。</summary>
public sealed record PointHealthResult
{
    public PointFreshnessResult Freshness { get; init; } = new();
    public PointAlarmResult Alarm { get; init; } = new();
    public HealthStatus HealthStatus { get; init; }
}

/// <summary>Point が属する gateway と、その egress 接続状態（#230 のハートビート由来）。</summary>
public sealed record PointGatewayInfo(string? Id, bool Connected);

/// <summary>
/// 一覧 1 行。台帳の静的メタデータ（名前・単位・階層・タグ）と判定済みの 3 軸を並べたもの。
/// 絞り込み・並べ替えは <see cref="PointHealthQueryFilter"/> がこの形のまま扱う。
/// </summary>
public sealed class PointHealthItem
{
    public string PointId { get; set; } = string.Empty;
    public string? PointDtId { get; set; }
    public string? Name { get; set; }
    public string? Unit { get; set; }
    public PointFreshnessResult Freshness { get; set; } = new();
    public PointAlarmResult Alarm { get; set; } = new();
    public PointGatewayInfo? Gateway { get; set; }
    public HealthStatus HealthStatus { get; set; }
    public string? DeviceDtId { get; set; }
    public string? DeviceName { get; set; }
    public string? SpaceDtId { get; set; }
    public string? SpaceName { get; set; }
    public string? FloorDtId { get; set; }
    public string? FloorName { get; set; }
    public string? BuildingDtId { get; set; }
    public string? BuildingName { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 一覧の絞り込み条件。**同一軸の複数指定は OR、軸をまたぐ指定は AND**。
/// 空のリスト / 空白文字列は「その軸で絞らない」を意味する。
/// </summary>
public sealed class PointHealthQuery
{
    public string? BuildingDtId { get; set; }
    public string? FloorDtId { get; set; }
    public string? DeviceDtId { get; set; }
    public string? GatewayId { get; set; }
    public IReadOnlyList<FreshnessStatus> Freshness { get; set; } = Array.Empty<FreshnessStatus>();
    public IReadOnlyList<AlarmStatus> Alarm { get; set; } = Array.Empty<AlarmStatus>();
    public IReadOnlyList<HealthStatus> HealthStatuses { get; set; } = Array.Empty<HealthStatus>();

    /// <summary>「この秒数以上来ていない」で絞る。**未受信（齢が無い）Point は齢 ∞ として必ず一致する**。</summary>
    public double? OlderThanSeconds { get; set; }

    /// <summary>customTags による絞り込み。複数指定は AND、大小無視。</summary>
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary>pointId / 名前の部分一致（大小無視）。前後空白は落とす。</summary>
    public string? Q { get; set; }

    public PointHealthSort Sort { get; set; } = PointHealthSort.Worst;
    public int Limit { get; set; } = 100;
    public int Offset { get; set; }
}

/// <summary><c>Total</c> は**ページング前**（絞り込み後）の件数。</summary>
public sealed record PointHealthPage(IReadOnlyList<PointHealthItem> Items, int Total);
