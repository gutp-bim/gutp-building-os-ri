namespace BuildingOS.Shared;

/// <summary>
/// 制御インプットのスキーマ情報（最小構成）。ポイントリスト（OxiGraph）を source of truth とし、
/// 制御 POST の入力値検証に用いる（#153）。
/// </summary>
public class ControlSchema
{
    /// <summary>
    /// データ型: "boolean", "number", "enum"
    /// </summary>
    public string DataType { get; set; } = string.Empty;

    /// <summary>
    /// enum型の場合のラベル（JSONオブジェクト文字列）。キーが許容コード集合を兼ねる。
    /// 例: {"1":"冷房","2":"暖房"}
    /// </summary>
    public string? EnumLabels { get; set; }

    /// <summary>number 型の許容下限（未設定なら下限なし）。</summary>
    public double? MinValue { get; set; }

    /// <summary>number 型の許容上限（未設定なら上限なし）。</summary>
    public double? MaxValue { get; set; }
}
