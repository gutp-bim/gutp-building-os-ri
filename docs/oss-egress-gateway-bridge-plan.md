# Egress/Ingress gRPC Gateway Bridge ＋ ControlType 汎用化 — 確定計画

## Context（なぜ・何を）

現状、機器制御の Egress は `PointController.Control` で **`Type = Hono` / `Body = {value}` をハードコード**しており（`DotNet/BuildingOS.ApiServer/Controllers/PointController.cs:75-77`、MVP）、プロトコルを増やせない。さらに既存の `DeviceControlType.BACnet` は実体が **Azure IoT Hub direct method** であり名前と実態が乖離している。

本計画は (1) ControlType をハードコードから **twin/設定駆動の汎用解決**へ、(2) 実態に合わせた **改名（Egress BACnet→Kandt）と DkConnect 削除**、(3) 接続先 [bbc-sim / BOWS コネクタ](https://github.com/takashikasuya/bacnet-sim-gateway)（北向き=BACnet/IP、Building OS へは MQTT/AMQP/gRPC）に向けた **スケーラブルな gRPC Gateway Bridge** の導入、を定義する。

接続先仕様の要点（bbc-sim 側 ADR-014/015, `docs/specs/northbound-bows-buildingos.md`）:
- BOWS は bbc-sim 北向き BACnet を読む**下流コネクタ**。Building OS へは telemetry を送り、将来は下り制御（`type=BACnet → WriteProperty`）を受ける。
- 識別は `localId = {tenant}/{deviceId}`、point_id は Building OS の OxiGraph で解決。
- **下り制御は bbc-sim 側でも将来課題** → 本計画はあちらの下り実装 issue と歩調を合わせる。

## 確定した設計判断

| 項目 | 決定 |
|---|---|
| ControlType 解決 | **専用コンフィグ駆動**（GatewayId プレフィックス／フィールド存在推測を廃止） |
| 設定ソース | **案A: ConfigMap/appsettings 初手** → `IGatewayConnectionTypeProvider` で抽象化し将来 DB(案B) へ昇格 |
| 新 ControlType | **`BacnetSim`** / connectionType **`bacnet-sim`**（❌`BACnet` は使わない＝あくまで Bacnet **Sim**） |
| Egress 改名 | `DeviceControlType.BACnet`→**`Kandt`**、`BacnetControlRequest`→`KandtControlRequest`、`BacnetDeviceControlHandler`→`KandtDeviceControlHandler`（**Egress 側のみ**） |
| 温存 | 上り telemetry の純正 BACnet 語彙（`building-os.raw.bacnet` / `bacnet-device-message` / `Point.*Bacnet` / `BacnetPointResolver`） |
| DkConnect | **コード削除**（handler / DTO / 分類 / DI / スキーマ解決） |
| gRPC ストリーム | **Ingress と Egress を分離**（別ストリーム・別 Pod） |
| Egress チャネル | **gateway ごとに 1 本の双方向 stream**（command↓ / result↑） |
| LB 基盤 | **Envoy 系 ingress**（BOWS は**クラスタ外＝建物エッジ**、north-south）。mTLS は cert-manager |
| スケール原則 | Ingress Pod 水平分割 / NATS へ即時 enqueue / **Pod 内に状態を持たない** / 短い keepalive / 定期 reconnect + jitter |

## 1. ControlType 汎用化（ハードコード撤廃）

```csharp
// 設定ソースを抽象化（ConfigMap 初手 → DB へ差し替え可能）
public interface IGatewayConnectionTypeProvider {
    string? Resolve(string gatewayId);   // "hono" | "bacnet-sim" | "kandt" / null=未登録
}

// twin の point/device ＋ connectionType → 送信仕様を解決
public interface IControlTypeResolver {
    Task<ControlDispatch?> ResolveAsync(Point point, Device? device, double value, CancellationToken ct);
}
public record ControlDispatch(string ControlType, string Body, string? GatewayId);  // null=制御不可
```

| connectionType | ControlType | Body ビルダ |
|---|---|---|
| `hono` | `Hono` | `{ "value": v }` |
| `bacnet-sim` | `BacnetSim` | `Point.*Bacnet` → BACnet WriteProperty パラメータ JSON |
| `kandt` | `Kandt` | `KandtControlRequest` JSON（IoT Hub direct method 用） |

`PointController.Control` は解決結果で `PointControlInfo { Type, Body }` を組み立てて publish。`NatsPointControlWorker` 以降（Type 文字列マッチ）は無改修。新プロトコルは「connectionType エントリ + Body ビルダ + 送信経路」を足すだけ。

## 2. 改名（Egress 側のみ）＋ DkConnect 削除

- 改名: 上記表のとおり。上り BACnet 語彙は触らない。
- 削除: `DkConnectDeviceControlHandler` / `DkConnectControlRequest`(+`DkConnectOperations`) / `OxiGraphControlSchemaResolver.IsDkConnectPoint`・`QueryDkConnectSchema` / `Program.cs` の DI / 関連 env ドキュメント。

## 3. gRPC Gateway Bridge（集約・スケーラブル）

専用 deployable `BuildingOS.GatewayBridge`（gRPC サーバ＋NATS ブリッジ）。ConnectorWorker には同居させない（重い NATS コンシューマ群とスケール単位を分離）。

```
   外部(建物エッジ)                クラスタ
 ┌───────────────┐  gRPC(mTLS)   ┌──────────────── Envoy ingress (gRPC L7 LB) ────────────────┐
 │ BOWS connector │──────────────▶│                                                            │
 │  (per gateway) │  ┌──Ingress stream ─▶ [Ingress Bridge Pod ×N] ─即時 enqueue─▶ NATS raw.bacnet
 │                │  └──Egress  bidi  ◀─▶ [Egress  Bridge Pod ×M] ◀─ NATS control.request.gw.{id}
 └───────────────┘                                              └─ result ─▶ NATS control.result.{id}
                                                                              ▲
                                              ApiServer(WaitForResult) ◀──────┘ (既存 result-bus 再利用)
```

### 3-1. ストリーム（分離）
- **Ingress**（telemetry 専用、client-streaming 想定）: `Telemetry` フレームを受領 → **即時に NATS `building-os.raw.bacnet` へ enqueue**（`bacnet-device-message` 準拠）→ 既存 ingress パイプライン無改修。Pod は水平分割・**状態を持たない**。
- **Egress**（gateway ごとに **1 本の双方向 stream**）: `Hello{gateway_id}` で確立 → サーバが `ControlCommand` を↓送出、クライアントが `ControlResult` を↑返却。

### 3-2. proto（`proto/` に追加）
```proto
service GatewayIngress {            // 分離・水平分割
  rpc StreamTelemetry(stream TelemetryFrame) returns (StreamAck);
}
service GatewayEgress {             // gateway ごと 1 本の双方向
  rpc Connect(stream EgressUp) returns (stream EgressDown);
}
message EgressUp   { oneof m { Hello hello = 1; ControlResult result = 2; } }
message EgressDown { ControlCommand command = 1; }
message Hello          { string gateway_id = 1; }
// point-id canonical (#181): the gateway resolves point_id → BACnet object/instance from the shared
// point list; the BACnet identity fields (3,4,5) were removed (reserved).
message ControlCommand { string control_id=1; string point_id=2; double present_value=6; int32 priority=7; }
message ControlResult  { string control_id=1; bool success=2; string response=3; }
```

### 3-3. レプリカ間ルーティング（状態を持たないための肝）
- Egress コマンドは「その gateway の stream を保持するインスタンス」に届く必要がある → **per-gateway subject** `building-os.control.request.gw.{gatewayId}`。
- 各 Egress Pod は **接続中の gateway の subject だけ** subscribe（メモリ上の接続 registry のみ。永続状態なし）。
- `IControlTypeResolver` が `GatewayId` を返し、publisher が gateway subject へ publish。LB がどの Pod に振っても NATS が正しい Pod へ fan-in。フェイルオーバ時は BOWS 再接続 → 再 subscribe。
- 結果は `building-os.control.result.{controlId}` → 既存 `NatsControlResultBus` → 既存 `WaitForResult` で Web へ。

### 3-4. 接続健全性 / 回復性
- **短い keepalive**（gRPC HTTP/2 keepalive；切断検出を速く）。
- **定期 reconnect + jitter**（BOWS 側。長命接続の偏り是正・thundering herd 回避）。
- Pod は使い捨て前提（再起動で registry 再構築、永続は NATS スパイン）。
- **オフライン即時検知（#186）**: per-gateway コマンドは Publish ではなく **NATS request** で送る。接続中レプリカは
  stream へ転送後に ack を返すため、未接続 gateway（購読者ゼロ）は NATS の **no-responders** として即座に表面化し、
  `PointController.Control` が **503**（metric `control.requests{result=gateway_offline}`）を返す。ack タイムアウト
  （レプリカは居るが ack が遅い）は Delivered 扱い＝誤検知しない安全側で、レース/遅延は従来どおり `WaitForResult`
  タイムアウトが backstop。JetStream durable 化（古い制御の遅延実行リスク）は採らず「即時エラー」のみ。in-process
  経路（Hono/Kandt = durable 生成）は対象外で常に Delivered。

### 3-5. LB / セキュリティ
- BOWS は**クラスタ外** → **Envoy 系 ingress**（Contour/Emissary 等）で north-south の gRPC L7 LB＋health check。
- **接続単位 LB**で十分（gateway=1 stream。コマンド到達は per-gateway subject が担保）。
- クライアント **mTLS は cert-manager** で運用。将来 in-cluster gRPC が増えれば Linkerd を被せる段階導入。

## 4. 段階実装（issue 化対象）

1. **ControlType 汎用化 + `IGatewayConnectionTypeProvider`(ConfigMap)**：ハードコード撤廃、twin/設定解決、テスト。
2. **Egress 改名（BACnet→Kandt）**：enum/DTO/handler/DI 改名、上り語彙は温存、テスト更新。
3. **DkConnect 削除**：handler/DTO/分類/DI/ドキュメント除去。
4. **`BacnetSim` 経路の Body ビルダ + connectionType=bacnet-sim 配線**（gRPC 前の準備）。
5. **`GatewayBridge` サービス雛形 + proto（Ingress/Egress 分離）**：gRPC サーバ、NATS 即時 enqueue（ingress）、per-gateway subject（egress）、結果は既存 result-bus。
6. **レプリカ間ルーティング + 接続健全性**：per-gateway subscribe、keepalive、reconnect+jitter、ステートレス化の検証。
7. **Envoy ingress + mTLS(cert-manager) + Helm/ArgoCD/OTel 配線**。
8. **bbc-sim/BOWS 結合試験**（あちらの下り実装 issue と同期）。

## 5. 検証

- 単体: `IControlTypeResolver` / `IGatewayConnectionRegistry` の解決（hono/bacnet-sim/kandt/未登録）。Body ビルダの BACnet WriteProperty 整形。
- 結合: GatewayBridge ↔ NATS（ingress 即時 enqueue、egress per-gateway 到達、result→WaitForResult）。レプリカ複数で gateway 分散時のコマンド到達。
- E2E: BOWS（or スタブ）↔ Envoy(mTLS) ↔ Bridge ↔ NATS ↔ ApiServer の往復（control_id 採番→結果通知）。
- 既存回帰: Hono Egress / 上り BACnet ingress が無改修で通ること。

## 6. Phase 2 — ゲートウェイ接続レジストリ（#154、実装済み）

「プロトコル」単位だった egress 抽象を「ゲートウェイインスタンス」単位へ拡張。同一プロトコル・別接続先の
ゲートウェイを複数ルーティングできるようにする。**ControlSchema（値の検証 #153）は不変**＝配送のみのリファクタ。

### モデル

```csharp
public sealed record GatewayConnection(
    string GatewayId, string BindingType, IReadOnlyDictionary<string,string> Settings);

public interface IGatewayConnectionRegistry {
    GatewayConnection? Resolve(string? gatewayId);   // 未登録は default binding にフォールバック
}
```

- **接続設定の正本は config 層**（`GatewayConnectionRegistryFactory.Create(IConfiguration)` が
  ApiServer / ConnectorWorker 双方で同一レジストリを構築）。twin/OxiGraph は静的メタデータ専任のまま。
- アダプタ（`IDeviceControlHandler`）は **env を直読みせず**、ルータが解決した `GatewayConnection` を
  `ExecuteControlAsync(info, connection, ct)` で受け取る。`connection.Settings` から接続先を読むので、
  同一 binding の2台が別ホストを向ける。
- ディスパッチは **BindingType ベース**。`NatsPointControlWorker` は wire 上の `Type` を信用せず、
  `info.GatewayId` から registry を再解決して binding を導出する（config 権威）。
- 後方互換: gateway 毎設定は binding の **デフォルト設定にマージ**（上書き）される。デフォルト設定は既存の
  単一インスタンス env（`HONO_AMQP_*` / `IOT_HUB_*`）からファクトリが合成する。よって `host` だけ上書きした
  gateway も残りの tenant/port/credentials を継承し、単一ゲートウェイ構成は無改修。gatewayId / 設定キーの
  照合は大文字小文字非依存。

### config サーフェス

| キー | 用途 |
|---|---|
| `GatewayConnectionTypes:Default` | 未マップ gateway の binding（default `hono`） |
| `GatewayConnectionTypes:Map:{gatewayId}` | gateway 毎 binding 上書き（`hono`/`kandt`/`bacnet-sim`） |
| `Gateways:{gatewayId}:Settings:{key}` | gateway 毎接続設定（hono: `host`/`port`/`tenant`/`user`/`password`/`tls`、kandt: `iotHubConnectionString`/`moduleId`） |

`credentialsRef`（外部 secret store の動的解決）は本スライス対象外＝予約。シークレットは config/env 直読み
（K8s Secret→env の既存パターンと整合）。

### 残課題

- **#186**: egress オフライン GW 検知 → 即時エラー応答（動的「接続状態」の共有。本レジストリ＝静的設定とは別層、別 PR）。
- Hono ハンドラ登録は引き続き `HONO_AMQP_HOST` の有無で活性判定（per-gateway 設定は接続先の上書きであって活性シグナルではない）。
