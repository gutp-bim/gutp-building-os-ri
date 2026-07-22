# コネクタ・ワーカー拡張ガイド

このガイドでは、Building OS OSS に新しいプロトコルコネクタを追加する手順を説明します。
既存の `BacnetConnectorWorker` を参考実装として用います。

---

## 1. アーキテクチャ概要

コネクタは `BackgroundService` として動作する NATS サブスクライバーです。

```
外部デバイス / プロトコル
        │
        ▼ NATS subject: building-os.raw.<protocol>
  ┌─────────────────────┐
  │  XxxConnectorWorker │  ← ConnectorWorkerBase を継承
  │  ProcessAsync()     │  ← プロトコル固有のパース・正規化
  └─────────────────────┘
        │
        ▼ NATS subject: building-os.validated.telemetry
  ParquetLakeWriterWorker → MinIO (Parquet レイク)
```

**ConnectorWorkerBase** の契約:

```csharp
// Subscribe → ProcessAsync → Publish の 3 ステップを基底クラスが担う
public abstract class ConnectorWorkerBase(
    IMessageSubscription subscription,   // NATS subscribe (building-os.raw.<protocol>)
    INatsPublisher publisher,            // NATS publish (building-os.validated.telemetry)
    string outputSubject,
    ILogger logger) : BackgroundService
{
    // 実装が必要なのはこれだけ
    protected abstract Task<string?> ProcessAsync(string rawMessage, CancellationToken ct);
    // 戻り値: 正規化済み ValidMessageJson 文字列、またはスキップなら null
}
```

`ProcessAsync` が例外を投げても基底クラスがログを記録してループを継続します。
処理結果は OpenTelemetry メトリクス（`connector.messages.processed`、`connector.process.duration`）
として自動計測されます。

---

## 2. 新規コネクタの作成手順

### ステップ 1 — JSON Schema を定義する

`DotNet/BuildingOS.Shared/Defines/Schemas/` に新しいスキーマファイルを追加します。

```bash
# 既存スキーマを参考にする
ls DotNet/BuildingOS.Shared/Defines/Schemas/
# bacnet-device-message.json
# hvac-device-message.json
# environmental-device-message.json ...
```

**例: `my-sensor-message.json`**

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "array",
  "items": {
    "type": "object",
    "required": ["deviceId", "timestamp", "value"],
    "properties": {
      "deviceId":  { "type": "string" },
      "timestamp": { "type": "string", "format": "date-time" },
      "value":     { "type": "number" }
    }
  }
}
```

### ステップ 2 — C# エンティティを生成する

```bash
cd Tools
./generate-dotnet-entities-from-schema.bash
```

`DotNet/BuildingOS.Shared/Defines/Entities/` に対応する C# クラスが生成されます。
**このディレクトリのファイルは手動編集しないでください。**

### ステップ 3 — ConnectorWorker を実装する

`DotNet/BuildingOS.ConnectorWorker/Connectors/` に新しいクラスを作成します。

```csharp
// MySensorConnectorWorker.cs
using BuildingOS.Shared.Entities;         // 生成済みエンティティ
using BuildingOS.Shared.Helpers;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using Corvus.Json;
using Microsoft.Extensions.Logging;

namespace BuildingOS.ConnectorWorker.Connectors;

public sealed class MySensorConnectorWorker(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    ILogger<MySensorConnectorWorker> logger)
    : ConnectorWorkerBase(
        subscription,
        publisher,
        outputSubject: "building-os.validated.telemetry",
        logger)
{
    protected override async Task<string?> ProcessAsync(
        string rawMessage,
        CancellationToken cancellationToken)
    {
        // 1. パース（生成済みエンティティを使用）
        var json = MySensorMessageJson.Parse(rawMessage);
        if (!json.IsValid()) return null;

        // 2. 正規化（ValidMessageJson 形式へ変換）
        var entities = new List<JsonAny>();
        foreach (var item in json.EnumerateArray())
        {
            entities.Add(ValidMessageJson.ValidTelemetryEntity.Create(
                id:       $"{item.DeviceId}.{DateTime.UtcNow.ToUnixTime()}",
                pointId:  item.DeviceId.ToString(),   // twin の pointId と一致させる
                building: new JsonString(string.Empty),
                datetime: item.Timestamp,
                value:    item.Value.AsNumber,
                name:     "MySensor",
                deviceId: item.DeviceId,
                data:     new ValidMessageJson.ValidTelemetryEntity.DataEntity([])));
        }

        if (entities.Count == 0) return null;

        // 3. ValidMessageJson 形式で返す（基底クラスが publish する）
        return ValidMessageJson.Create(
            new ValidMessageJson.ValidTelemetryEntityArray([.. entities])).ToString();
    }
}
```

**ValidMessageJson のフィールド契約は [telemetry-specification.md](../architecture/telemetry-specification.md) を参照してください。**

### ステップ 4 — DI に登録する

`DotNet/BuildingOS.ConnectorWorker/Startup/ConnectorWorkerServiceCollectionExtensions.cs` の
`AddConnectorWorkerMessaging` メソッドに追加します。

```csharp
public static IHostApplicationBuilder AddConnectorWorkerMessaging(
    this IHostApplicationBuilder builder)
{
    // 既存のコネクタ登録 ...
    builder.Services.AddSingleton<IMessageSubscription>(sp =>
        new NatsSubscription(sp.GetRequiredService<INatsConnection>(),
            "building-os.raw.my-sensor"));    // ← サブジェクト名

    builder.Services.AddHostedService<MySensorConnectorWorker>();  // ← 追加

    return builder;
}
```

NATS サブジェクト → ワーカー対応表（既存）:

| サブジェクト | ワーカー |
|------------|---------|
| `building-os.raw.hvac` | `HvacConnectorWorker` |
| `building-os.raw.bacnet` | `BacnetConnectorWorker` |
| `building-os.raw.environmental` | `EnvironmentalConnectorWorker` |
| `building-os.raw.electric` | `ElectricConnectorWorker` |
| `building-os.raw.behavior` | `BehaviorConnectorWorker` |
| `building-os.raw.my-sensor` | `MySensorConnectorWorker` ← 追加 |

### ステップ 5 — ユニットテストを書く

`DotNet/BuildingOS.Shared.Test/` にテストを追加します。

```csharp
// MySensorConnectorWorkerTest.cs
public class MySensorConnectorWorkerTest
{
    [Fact]
    public async Task ProcessAsync_ValidMessage_ReturnsValidJson()
    {
        var subscription = new MockMessageSubscription();
        var publisher    = new MockNatsPublisher();
        var logger       = new NullLogger<MySensorConnectorWorker>();

        var worker = new MySensorConnectorWorker(subscription, publisher, logger);

        var rawMessage = """
            [{"deviceId":"dev-001","timestamp":"2026-01-01T00:00:00Z","value":25.5}]
            """;

        var result = await worker.InvokeProcessAsync(rawMessage, CancellationToken.None);

        Assert.NotNull(result);
        var validated = ValidMessageJson.Parse(result!);
        Assert.True(validated.IsValid());
    }
}
```

```bash
cd DotNet
dotnet test BuildingOS.Shared.Test --filter "MySensor"
```

---

## 3. gRPC GatewayIngress 経由での取り込み（推奨経路）

デバイスとの点リスト（point list）を共有できる場合は、プロトコル固有コネクタではなく
**gRPC GatewayIngress** の使用を検討してください。

- ゲートウェイ側でプロトコルアドレス → `point_id` を解決し、`TelemetryFrame` を送信
- `GRPC_INGRESS_PORT=5051` を設定した ConnectorWorker がフレームを受け取り、
  デジタルツインから静的メタデータを補完して `building-os.validated.telemetry` に発行
- プロトコル固有コネクタ（raw.xxx ホップ）が不要になる

詳細は [gateway-integration.md](gateway-integration.md) を参照してください。

---

## 4. MQTT / AMQP 経由のデバイス接続

point リストを共有しないデバイス（localId → pointId の動的解決が必要）には
Mosquitto（MQTT）または Eclipse Hono（AMQP Northbound）経由の接続が使えます。

```bash
# Mosquitto プロファイルで起動
MQTT_HOST=building-os.mosquitto \
  docker compose -f docker-compose.oss.yaml --profile mqtt up -d
```

デバイスシミュレータ（`Tools/development-edge-device/`）が MQTT で telemetry をパブリッシュし、
`MqttIngressWorker` が受信して `building-os.raw.mqtt` に流します。

設計詳細は [oss-hono-design.md](../architecture/oss-hono-design.md) を参照してください。

---

## 5. テレメトリ契約

`ProcessAsync` が返す JSON は `ValidMessageJson` フォーマットに準拠する必要があります。
主要フィールド:

| フィールド | 型 | 説明 |
|----------|-----|------|
| `id` | string | `{pointId}.{unixtime}` を推奨 |
| `pointId` | string | デジタルツインの `point_id`（必須・NOT NULL） |
| `building` | string | ビル識別子（省略可・空文字列でも可） |
| `datetime` | ISO 8601 | タイムスタンプ（必須） |
| `value` | number | テレメトリ値（必須） |
| `name` | string | 点の名称（省略可） |
| `deviceId` | string | デバイス識別子 |
| `data` | object | プロトコル固有の追加フィールド（任意） |

詳細は [telemetry-specification.md](../architecture/telemetry-specification.md) を参照してください。

---

## 関連ドキュメント

- [telemetry-specification.md](../architecture/telemetry-specification.md) — `ValidMessageJson` フィールド契約
- [oss-nats-design.md](../architecture/oss-nats-design.md) — NATS サブジェクト・ストリーム設計
- [gateway-integration.md](gateway-integration.md) — gRPC GatewayIngress（推奨取り込み経路）
- [oss-hono-design.md](../architecture/oss-hono-design.md) — MQTT / AMQP Northbound 接続設計
- [ADR-0001](../adr/0001-at-most-once-connector-delivery.md) — 配信保証の設計判断
