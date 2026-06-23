# テレメトリ仕様

このドキュメントは、Building OS OSS の ConnectorWorker がプロトコル別 payload
を処理した後に publish する正規化済み telemetry contract を定義します。

## Source Of Truth

正本の schema は
`DotNet/BuildingOS.Shared/Defines/Schemas/valid-message.json` です。

生成済み C# entity は `DotNet/BuildingOS.Shared/Defines/Entities/` にあります。
このディレクトリは手編集せず、schema 変更時は次のコマンドで再生成します。

```bash
cd Tools
./generate-dotnet-entities-from-schema.bash
```

## Message Envelope

正規化済み telemetry message は、必須の `telemetries` array を持つ JSON object
です。

```json
{
  "telemetries": [
    {
      "id": "point-001.1710000000",
      "device_id": "device-001",
      "point_id": "building/floor/room/temp",
      "building": "building-a",
      "value": 24.5,
      "data": {
        "source": "hvac"
      },
      "datetime": "2026-05-18T00:00:00Z",
      "name": "Room temperature"
    }
  ]
}
```

## Field Contract

| Field | Required | Type | Description |
|---|---:|---|---|
| `telemetries` | yes | array | 正規化済み telemetry row の batch |
| `telemetries[].device_id` | yes | string | source device identifier |
| `telemetries[].point_id` | yes | string | API と TimescaleDB で使う canonical point identifier |
| `telemetries[].building` | yes | string | point が属する building identifier / name |
| `telemetries[].value` | yes | number | point の代表 numeric value |
| `telemetries[].data` | yes | object | protocol-specific attribute を保持する JSON object |
| `telemetries[].datetime` | yes | date-time string | ISO 8601 形式の observation timestamp |
| `telemetries[].id` | no | string | tracing / deduplication 用の message または row identifier |
| `telemetries[].name` | no | string | human-readable point name |

schema は追加の top-level field や telemetry field を許可しません。
protocol-specific field は `data` に格納します。

## NATS Subjects

Raw protocol message は protocol-specific subject から NATS に入ります。

| Subject | Payload schema |
|---|---|
| `building-os.raw.hvac` | `hvac-device-message.json` |
| `building-os.raw.bacnet` | `bacnet-device-message.json` |
| `building-os.raw.environmental` | `environmental-device-message.json` |
| `building-os.raw.electric` | `electric-device-message.json` |
| `building-os.raw.behavior` | `behavior-sensor-message.json` |

Connector worker は正規化済み message を次の subject に publish します。

```text
building-os.validated.telemetry
```

stream / retry 設計の詳細は
[`oss-nats-design.md`](oss-nats-design.md) を参照してください。

## Storage Mapping

正規化済み telemetry は TimescaleDB の `telemetry` hypertable に対応します。

| Valid message field | TimescaleDB column |
|---|---|
| `datetime` | `time` |
| `point_id` | `point_id` |
| `building` | `building` |
| `device_id` | `device_id` |
| `name` | `name` |
| `value` | `value` |
| `data` | `data` JSONB |
| `id` | `id` |

圧縮、保持、cold export policy は
[`oss-timescaledb-schema.md`](oss-timescaledb-schema.md) を参照してください。

## Connector Rules

- publish 前に raw payload を protocol schema で検証します。
- invalid または unmapped な raw message は、partial telemetry として publish
  せず drop します。
- timestamp は UTC を使います。Device が observation timestamp を持つ場合は
  `datetime` に保持し、持たない場合は ingestion time を使います。
- `value` は numeric に保ちます。非数値状態は安定した numeric code と
  `data` 内の metadata で表現します。
- protocol-specific attribute は `data` に入れ、shared API と storage schema
  を安定させます。
- source protocol から deduplication に十分な field が得られる場合は、
  deterministic な `id` を使います。

## 永続化保証（#187）

gRPC ingress（GatewayIngress → `NatsIngressTelemetryBus`）は **JetStream publish-ack** で publish するため、
`StreamAck.accepted` は「ストリームへ永続化済み」を意味する（at-most-once の解消）。ack が取れなかった
フレームは accepted されず、`publish_failed` メトリクスを計上してストリームは継続する。

> 既知の残件: 他の validated.telemetry 生産者（HVAC/BACnet 等のコネクタワーカー、および MQTT/Hono の
> `raw.*` ingress）は依然として core publish（fire-and-forget）であり、ack を取っていない。共有 `NatsPublisher`
> は `control.result`（非 JetStream subject）にも使われるため一律切替はできず、全生産者の at-least-once 化は
> 後続課題とする。

## 値型の設計判断（#189）

`TelemetryFrame.value` / `valid-message.json` の `value` は **数値単一型**で確定しています。
状態系ポイント（運転モード・列挙・ON/OFF 等）は次の方針で扱います。

- **boolean** → ゲートウェイ側で `0` / `1` に正規化。
- **enum / 状態コード** → 安定した **数値コード**で送り、ラベルは ControlSchema / ポイントリストの
  メタ（`EnumLabels` 等）で解決する。
- **文字列状態の補助情報** → `attributes`（→ validated の `data`）に文字列で添える（型情報は落ちる前提）。

理由: `valid-message.json` / TimescaleDB / KV hot store / proto まで数値前提で**パイプライン全体の整合**が
取れており、現行要件（建物設備テレメトリ）は数値コード化で充足する。文字列/列挙の**一級対応**（proto の
`oneof value`、storage schema、KV の波及）は影響範囲が大きいため、独立した設計フェーズに先送りする（#189）。

## Documentation Coverage Notes

このドキュメントは正規化済み telemetry contract を扱います。Raw protocol
payload の詳細は、code generation の source of truth である JSON Schema
files に保持します。
