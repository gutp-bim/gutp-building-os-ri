using System.Text.Json.Serialization;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// 最終受信インデックスの状態（#452）。
///
/// <para><c>Warming</c> は「まだ全件読み切っていない＝エントリが無いことが欠測の証拠にならない」、
/// <c>Ready</c> は「初期リプレイ完了＝エントリが無い Point は本当に一度も来ていない」、
/// <c>Degraded</c> は「watch が切れて追随できていない＝手元の値は使えるが完全ではない」。</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PointLastSeenIndexState
{
    Warming,
    Ready,
    Degraded,
}

/// <summary>
/// 1 Point ぶんの最新受信。<c>LastSeen</c> は timestamp が読めなかった場合 null になる
/// （エントリごと捨ててしまうと「一度も来ていない」と区別できなくなるので残す）。
/// </summary>
public sealed record PointLastSeenEntry(string PointId, DateTimeOffset? LastSeen, double? Value, string? ValueType);

/// <summary>
/// pointId → 最終受信のプロセスローカルな読み取り面（#452）。データ健全性 API は数千 Point ぶんの
/// 最新値をここから **1 回のメモリ参照ずつ**で引く（<c>IHotTelemetryStore</c> の単点 Get を
/// Point 件数ぶん叩かない）。新しいデータストアではなく、既存の <c>telemetry-latest</c> KV の写像。
/// </summary>
public interface IPointLastSeenIndex
{
    PointLastSeenIndexState State { get; }

    /// <summary>最後に watch イベントを取り込んだ / リプレイ完了を確認した時刻。未同期なら null。</summary>
    DateTimeOffset? LastSyncAt { get; }

    int Count { get; }

    bool TryGet(string pointId, out PointLastSeenEntry entry);
}
