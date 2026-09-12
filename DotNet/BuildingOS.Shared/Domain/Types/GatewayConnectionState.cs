using System.Text.Json.Serialization;

namespace BuildingOS.Shared.Domain.Types;

/// <summary>
/// ゲートウェイの egress 接続状態（ADR-0004 の「接続」軸）。**3 値であることが仕様**（#463）。
///
/// <para><c>Disconnected</c>（heartbeat が無い＝観測上つながっていない）と
/// <c>Unknown</c>（そもそも確かめられなかった）は**違う事実**で、同じ値に丸めてはいけない。
/// 丸めると、NATS KV が一時的に読めないだけで全ゲートウェイが「切断」に見え、`/health` の欠測理由も
/// 全件「ゲートウェイ切断」になる。運用者は存在しない障害を追いかけ、真因（読めていないこと）は
/// ログの warning にしか残らない。</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GatewayConnectionState
{
    /// <summary>生きた heartbeat がある。</summary>
    Connected,

    /// <summary>heartbeat が無い（TTL 切れ / 未登録）＝観測上つながっていない。</summary>
    Disconnected,

    /// <summary>接続状態を確かめられなかった（KV 不達など）。切断ではない。</summary>
    Unknown,
}

/// <summary>接続状態を API の露出形に落とす変換。</summary>
public static class GatewayConnectionStateExtensions
{
    /// <summary>
    /// API が返す <c>bool?</c>（<c>null</c> = 不明）へ。この形は同じ行に並ぶ
    /// <c>GatewayAdminView.PointlistSynced</c> の tri-state（#230 Phase 2b）に揃えてある。
    /// </summary>
    public static bool? ToConnectedFlag(this GatewayConnectionState state) => state switch
    {
        GatewayConnectionState.Connected => true,
        GatewayConnectionState.Disconnected => false,
        _ => null,
    };
}
