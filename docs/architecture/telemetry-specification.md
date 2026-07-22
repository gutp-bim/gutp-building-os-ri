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

## 値型（#152 で一級化 / #189 は履歴）

`TelemetryFrame.value` / `valid-message.json` の `value` は **判別ユニオン `number | string | boolean`**
です（#152・ADR-0006）。状態系ポイント（運転モード・列挙・ON/OFF・文字列ステータス）は**一級型として
そのまま送れます**。

- **number**（既定・主型）→ チャート／集計（avg/min/max）の対象。数値は全経路で完全後方互換。
- **string**（状態ラベル・列挙ラベル）→ `TelemetryFrame` の `value_str`（proto oneof, field 6）。
  検証済みメッセージでは `value` が JSON 文字列になり、永続化層は `value_type="string"` + `value_text`
  で判別を運ぶ。集計は last-in-bucket（Phase B, D3）で「その区間の最新状態」を代表値にする（数値集計は null）。
- **boolean**（ON/OFF）→ `TelemetryFrame` の `value_bool`（proto oneof, field 7）。永続化は
  `value_type="boolean"` + `value_bool`。UI は最新値・状態タイムラインで ON/OFF 表示。

判別子（`value_type`）が無い旧データ・旧行は **number とみなす**（後方互換の既定）。frontend の数値チャートは
数値のみ、非数値は状態タイムライン（`TelemetryStateTimeline`）に表示される。

### 旧回避策（#189）の deprecate

#152 以前は数値単一型だったため、状態系は次の**回避策**で扱っていました。**#152 でこれらは不要になり、
非推奨（deprecated）です** — 新規ポイントは上記の一級型で送ってください。

- ~~**enum / 状態コード** → 安定した数値コードで送り、ラベルは表示側メタ（テレメトリ表示用の `labels`）で
  index 解決する~~ → **非推奨**。enum は**ラベル文字列を `value_str` で直接送る**（`string` 一級型）。
  frontend の数値+`labels` index 表示（`telemetry-hot-data` の `splitLabels`）は後方互換のため残置するが、
  一級 string 値が来た場合はそちらを優先表示する（既に実装済み）。
- ~~**boolean** → ゲートウェイ側で `0/1` に正規化~~ → **非推奨**。`value_bool` で真偽を直接送る
  （`0/1` 正規化も後方互換で引き続き数値として通るが、状態の意味は落ちる）。
- **文字列状態の補助情報** → `attributes`（→ validated の `data`）に添える路は**引き続き有効**（主値では
  ない付随情報の置き場として）。主たる状態値は `value_str` を使うこと。

> **注意（control 側は別物・非推奨ではない）**: `ControlSchema.EnumLabels`（JSON `{"1":"冷房",...}`）は
> **書き込み可能な多状態出力（multi-state output）の制御**で許可コード集合とラベルを定義するもので、
> テレメトリ表示の回避策とは別の一級機能です。`ControlValueValidator` / 制御モーダルで使われ続けます。
> ここで deprecate するのは**テレメトリ読取側の数値コード表現**のみです。

## Documentation Coverage Notes

このドキュメントは正規化済み telemetry contract を扱います。Raw protocol
payload の詳細は、code generation の source of truth である JSON Schema
files に保持します。
