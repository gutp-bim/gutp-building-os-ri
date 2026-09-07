# gRPC gateway-ingress の受理/拒否ポリシー（#292）

Building 階層（`Building` → `Room`/`Level` → `EquipmentExt`）が未確定なポイントのテレメトリーを
gRPC `GatewayIngress`（`DotNet/BuildingOS.ConnectorWorker/Connectors/GatewayIngressService.cs`）が
どう扱うかの方針。実装は既に完成しており（`IngressHierarchyPolicy` / `IngressHierarchyOptions`、
`GRPC_INGRESS_REQUIRE_HIERARCHY`）、本メモは**方針そのもの**（reject-over-quarantine の理由、
再送の要否、可観測性）を明文化する。

> 関連: `docs/architecture/gateway-bridge-ingress-egress-split.md`（ingress/egress の分割）、
> `docs/architecture/oss-gateway-pointlist-sync.md`（point list 同期）。CLAUDE.md の
> `GRPC_INGRESS_REQUIRE_HIERARCHY` 環境変数表に技術的な詳細（判定ロジック、既定値）がある。

## ゲート（TryIngestAsync の判定順）

`GatewayIngressService.TryIngestAsync` は各フレームを以下の順に判定し、最初に該当したゲートで
拒否する（`building_os.ingress.messages` カウンター、`result` ラベルにゲート名が入る）。

1. `missing_id` — `gateway_id` / `point_id` のいずれかが空。
2. `identity_missing` / `identity_mismatch`（#296）— mTLS ingress が検証した gateway id と
   フレームの `gateway_id` が一致しない（`GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY` 有効時のみ強制）。
3. `unknown_point` — twin に `point_id` が存在しない。
4. `gateway_mismatch` — twin 上の所有 gateway と送信元 `gateway_id` が不一致。
5. `no_building_path` / `no_device_link`（#292、本メモの対象）— `IngressHierarchyPolicy` が
   `GRPC_INGRESS_REQUIRE_HIERARCHY` 有効時のみ判定。ポイントが building 階層（
   `locatedIn`→`Room`→`Level`→`Building` の空間チェーン、または `sbco:floor` リテラル結合の
   いずれか）に到達できない、またはデバイスへリンクされていない場合に拒否。
6. 上記いずれも通過 → NATS へ publish。publish 自体が失敗した場合のみ `publish_failed`。

`GRPC_INGRESS_REQUIRE_HIERARCHY` は既定 off。ツインは段階的にモデリングされる（#118）ため、
階層未確定のポイントを既定で全滴下すると初期構築中のデプロイでテレメトリーが全て消える。
本番で twin のモデリングが完了した段階で on にする opt-in ポリシー。

## 決定: reject（拒否）であり、quarantine/dead-letter ではない

検討したが採用しなかった選択肢と、reject を選んだ理由:

- **quarantine（隔離ストア）や dead-letter subject に退避して後で replay する** ことは実装しない。
  理由: ゲートウェイは同じポイントのテレメトリーを継続的に送信し続けるデバイスであり、
  一度拒否されたフレームだけを後から再送する仕組みを別に持つ意味が薄い。
  twin の階層を修正すれば、次にゲートウェイが送ってくる同じポイントのテレメトリーがそのまま
  受理されるようになる — つまり「リカバリー」は BuildingOS 側の replay 機構ではなく、
  **ゲートウェイの通常の継続送信**が担う。
- 拒否されたフレームはログ + メトリクスに記録されるのみで、消費側に一切残らない
  （NATS へ publish されない）。ストレージ・運用コストを増やさない。
- この判断は #292 のスコープ決定であり、quarantine ストア・dead-letter subject・replay API は
  意図的に対象外（out of scope）。

## 可観測性: メトリクス・ログ・管理画面

- **メトリクス**: `building_os.ingress.messages`（Prometheus 上は
  `building_os_ingress_messages_total`）、タグ `source=gateway-grpc` / `gateway` / `result`。
  `result` には `no_building_path` / `no_device_link` を含む全ゲートの理由が入る
  （`DotNet/BuildingOS.Shared/Infrastructure/Telemetry/BuildingOsMetrics.cs`）。
- **ログ**: 各ゲートで `LogWarning`（構造化ログ、理由・`gateway_id`・`point_id` 付き）。
- **管理画面（#292 で追加）**: `GET /api/system/ingress-rejections`（管理者のみ、
  `IngressRejectionStatsService` が上記 Prometheus カウンターを `result` でグルーピングして
  `published` 以外を返す）と、web-client の `/platform/ingress-rejections`
  （`IngressRejectionsView` / `IngressRejectionsDashboard`）。他の `/platform/*` 画面
  （`status` / `config` / `settings`）と同じく、Prometheus 未配線時は件数を出さずグレースフル
  デグレードする。

## 非対象

- quarantine ストア・dead-letter subject の新設。
- BuildingOS 側での拒否フレームの replay/再送 API。
- 拒否理由の重要度づけ・アラート閾値（既存の Prometheus/Grafana 側の一般的な仕組みに委ねる）。
