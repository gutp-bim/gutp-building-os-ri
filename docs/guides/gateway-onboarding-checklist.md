# ゲートウェイ・オンボーディング・チェックリスト

*[新規ゲートウェイを Building OS に接続する最短手順。詳細は各リンク先を参照。]*

新規ゲートウェイを「認識させる」導線は現状 UI 上の単一ボタンではなく、以下の暗黙の設定
(twin へのポイント登録 + 制御 binding)が揃うことで成立します(#127)。このチェックリストは
[gateway-integration.md](gateway-integration.md)・[onboarding-e2e-gateway.md](onboarding-e2e-gateway.md)・
[oss-gateway-pointlist-sync.md](../architecture/oss-gateway-pointlist-sync.md) に散在する手順を1枚に集約したもので、
新しい仕組みを導入するものではありません。

## チェックリスト

- [ ] **1. デジタルツインに `sbco:gatewayId` を持つ Point を登録する**
  ゲートウェイが所有する Point(SBCO の `sbco:PointExt`)に `sbco:gatewayId` を設定し、twin に
  投入します(`/admin/twin` からのアップロード、または起動時シード `OXIGRAPH_SEED_TTL_PATH`)。
  **制約: 1つの `gateway_id` は1つの building にのみ所属できます** — 違反すると起動時シードは
  fail-fast、`/admin/twin` 経由は 409 を返します。
  詳細: [onboarding-e2e-gateway.md](onboarding-e2e-gateway.md)の「A-3. デジタルツインに設備を入れる」節、
  Turtle の最小構成例は [getting-started.md](getting-started.md)の §4。

- [ ] **2. 制御 binding を設定する(制御を使う場合)**
  `GatewayConnectionTypes__Map__{gatewayId}` env に `hono` / `kandt` / `bacnet-sim` のいずれかを
  設定します(既定 compose は `gw-001: bacnet-sim` の1件のみ — 新しい `gatewayId` を追加する場合は
  この env を自分で追記してください)。ホスト/資格情報が既定と異なる場合は
  `Gateways__{gatewayId}__Settings__{key}` で個別上書きできます(`host`/`port`/`tenant`/`user`/
  `password`/`tls` など、binding ごとに異なるキー)。
  詳細: [CLAUDE.md](../../CLAUDE.md) の Gateway connection registry の節、
  [gateway-integration.md](gateway-integration.md)の「5. ローカルで試す」節。

- [ ] **3. Ingress(テレメトリ)/ Egress(制御)の到達性を確認する**
  Ingress は `GatewayIngress`(ConnectorWorker、`GRPC_INGRESS_PORT` 既定 5051、**未設定だと
  リスナーなし**)、Egress は `GatewayEgress`(GatewayBridge、常時 :5052 待受)— 別サービス・
  別ポートです。ゲートウェイ側の設定で ingress/egress を取り違えると、テレメトリは正常に
  流れるのに制御だけが無言で届かない、という気づきにくい失敗になります。
  詳細: [gateway-integration.md](gateway-integration.md)(ポート表・mTLS 境界)、
  [onboarding-e2e-gateway.md](onboarding-e2e-gateway.md)の「Step D — nexus-gateway を Building OS に
  つなぐ」節。

- [ ] **4. ゲートウェイ側で Point List 同期を配線する**
  `GET /gateways/{gatewayId}/pointlist` を `If-None-Match` 付きでポーリングします(twin が
  正本、content-hash ETag、`?since={etag}` で差分取得も可)。twin 更新時は
  `building-os.pointlist.updated.gw.{id}` → GatewayBridge の `EgressDown{PointListUpdate}` で
  push 通知も届きますが、これは最適化であり ETag ポーリングが正のリライアビリティです。
  詳細: [oss-gateway-pointlist-sync.md](../architecture/oss-gateway-pointlist-sync.md)(API 仕様・mTLS 信頼ヘッダ・
  検証チェックリスト)。

## 既知の制約

- **GatewayBridge に HTTP ヘルスエンドポイントがありません**(gRPC h2c のみ)。オンボード後の
  死活監視は現状 gRPC 接続そのものの成否でしか判断できません — HTTP `/health` の追加は別途の
  実装課題です(未着手)。
- **UI からの「ゲートウェイを登録する」ボタンはありません**(`/admin/gateways` は read-only +
  `resync-pointlist` のみ — binding/twin の正本は GitOps/twin 側にあるという設計方針のため)。
  本チェックリストはその代替として、必要な設定変更点を手順化したものです。
