# E5 — Point List / Digital Twin 整合性（本評価の独自性）

## 目的
Building OS と gateway の契約境界に **(gateway_id, point_id)** を置くことで、現場プロトコルの差異を
隠蔽しつつ Digital Twin（OxiGraph）側で設備・空間・単位・制御可否を一元管理できることを示す。

## 計測指標
- point resolution success rate: local_id → point_id 解決成功率（gateway 側）。
- unresolved local_id count / unresolved ratio（全 telemetry に対する未解決率）。
- **point ownership rejection count**: gateway が所有しない point への送信拒否数（GatewayIngress で
  gateway_id が point を所有しない frame は skip+メータ、CLAUDE.md 参照）。
- point list sync latency: Building OS の Point List 更新 → gateway 反映まで。
- remapping correctness: mapping 変更後に期待 point_id へ入った割合。
- twin lookup latency p95: point_id → building/device/space/unit（`IPointMetadataCache` 経由）。
- metadata completeness: unit/device/space/writable 等が揃う point の割合。

## シナリオ
1. **unknown point_id**: point list に無い id を送信 → 受理ゼロ（skip+メータ）を確認。
2. **ownership 違反**: 別 gateway 所有の point を送信 → 拒否を確認。
3. **remap**: point list の mapping を変更 → 反映後に正しい point_id へ入るか。
4. **deleted point**: 削除済み point への送信 → unresolved 計上。
5. **unit change**: unit 変更が twin lookup / 表示に反映されるか。

## 合否（kpi-thresholds.yaml: E5_pointlist_integrity）
resolution success ≥ 99.9% / unknown・ownership 拒否 100% / remap correctness 100% / twin lookup p95 < 50ms。

## 既存資産・ギャップ
- 既存: `s4_quality.sh`, `quality_checker.py`（validation error rate の一部）。
- **実装済**: `Tools/e2e-performance/s10_pointlist_integrity.{py,sh}`（下記）。
- **残ギャップ**: deleted point の unresolved 計上 / unit change の表示反映 / point list sync latency
  （push 配信のエンドツーエンド遅延、#224 の push が前提）。

## 実装メモ（2026-06-15, parquet 既定・ローカル）

### ハーネス `s10_pointlist_integrity.{py,sh}`
gRPC `GatewayIngress`（point-id 正準, #181）を直接叩く。`StreamAck.accepted` は**ストリーム単位**なので、
カテゴリごとに別の client-stream を張り、accept 数からカテゴリ別の受理/拒否率を直接導く（Prometheus 不要）:

| ストリーム | 送信内容 | 期待 |
|---|---|---|
| valid | (GW_A, GW_A 所有 point) ×N | accepted == N |
| unknown | (GW_A, 未 seed id) ×N | accepted == 0 |
| ownership | (GW_A, GW_B 所有 point) ×N | accepted == 0 |
| missing | (空 gateway/point) ×N | accepted == 0 |
| remap | point を GW_A→GW_B に付け替え + キャッシュ flush | (GW_A,p) 拒否 & (GW_B,p) 受理 |

twin は SPARQL で直接 seed/remap/cleanup。`IPointMetadataCache` は TTL 5min + miss-refresh 30s なので
新規 seed は ~30s 以内に可視化（polling で確認）。remap は既存 id 更新で miss にならないため、
connector-worker を restart してキャッシュを確定 flush（`--no-restart` で抑止可）。
twin lookup p95 は `GET /telemetries/query?...&latest=true` の p95 を proxy 計測（Hot KV/lake も触るため上界）。

### 発見・修正した本番バグ: connector-worker に `OXIGRAPH_ENDPOINT` 未設定
gRPC ingress 経路は OSS スタックで初めて有効化（従来 `GRPC_INGRESS_PORT` 未設定で listener 無し）。
有効化すると `PointMetadataCache` が OxiGraph 既定 `localhost:7878`（コンテナ内では誤り）へ接続し、
全 frame が metadata 解決失敗 → handler 例外。`docker-compose.oss.yaml` の connector-worker に
`OXIGRAPH_ENDPOINT: http://building-os.oxigraph:7878` を追加して解消。

### 実測（parquet, 200 frames/カテゴリ, 1 ローカル stack）

| 指標 | 実測 | 閾値 | 判定 |
|---|--:|--:|---|
| point_resolution_success | 1.0 | ≥ 0.999 | ✅ |
| unknown_point_rejection | 1.0 | == 1.0 | ✅ |
| ownership_rejection | 1.0 | == 1.0 | ✅ |
| remapping_correctness | 1.0 | == 1.0 | ✅ |
| twin_lookup_p95_ms | 9.33 ms | < 50 ms | ✅ |

ヘッドライン指標 `point_resolution_success` を含む E5 全 KPI が閾値内。実行: `bash Tools/e2e-performance/s10_pointlist_integrity.sh`
または `bash e2e/runner/run-axis.sh E5 --out <dir>`。
