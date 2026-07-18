# 障害対応 Runbook（#163）

Building OS OSS の主要依存（NATS / MinIO / PostgreSQL / Keycloak / ゲートウェイ）が落ちたときの
**症状 → 切り分け → 一次対応 → 復旧**を1ページに集約する。オンコールが最初に開く1枚を目指す。

> ⚠️ **この Runbook はコンポーネントの健全性契約（health エンドポイント・ストリーム/ack 挙動・
> フェイルファスト経路）に基づいて記述していますが、各障害を実機で注入した検証は未実施です。手順・
> 復旧時間の目安は本番/ステージングで実際のゲームデー（障害演習）で確認してください。**

関連: [oss-backup-restore-runbook.md](oss-backup-restore-runbook.md),
[oss-production-deployment.md](oss-production-deployment.md), [oss-tier-architecture.md](oss-tier-architecture.md),
[system-architecture.md](system-architecture.md), [oss-sla-freshness.md](oss-sla-freshness.md)。

---

## 0. 最初に見るところ（トリアージ）

| 見るもの | どこ |
|---|---|
| API 生存 | `GET http://<api>:5000/health`（匿名） |
| ConnectorWorker readiness | `GET http://<worker>:8081/health/ready`（`ready`=NATS 接続 Open, #145） |
| サービス別 up/down | `GET /api/system/status`（`SYSTEM_STATUS_HEALTH_TARGETS` の `/health` ファンアウト） |
| NATS 状態 | `http://<nats>:8222/varz`・`/jsz`（JetStream） |
| メトリクス（任意） | Prometheus/Grafana（`--profile observability` 時のみ。既定では無し） |

**依存の効き方（要点）:**
- **テレメトリ取り込み**は NATS 経由。NATS/MinIO が落ちても **JetStream が未 ack 分を保持**するため、
  復旧後に取りこぼしなく流れ直す（下記）。
- **最新値（Hot）**は NATS KV `telemetry-latest`。**range 読み取り**は MinIO レイク。
- **制御**は NATS request（オンライン即応、オフラインは 503 fast-fail, #186）。

---

## 1. NATS が落ちた

**症状**: connector-worker `/health/ready` が 503、テレメトリが止まる、制御が失敗（NATS no-responders）、
operator-home が全 Point stale/missing 化。

**切り分け**: `http://<nats>:8222/varz` 応答なし → NATS 本体ダウン。応答あるが `/jsz` 異常 → JetStream 問題。

**一次対応 / 復旧**:
1. NATS を再起動（`docker compose ... restart building-os.nats` / K8s は Pod 再作成）。JetStream は
   **`nats_data` ボリュームにファイル永続**（`oss-stack/nats/nats-server.conf`）なので、ストリームと
   未 ack メッセージは再起動後も残る。
2. connector-worker / api は NATS 復帰で自動再接続（readiness が Open に戻る）。
3. **データロス評価**: `BUILDING_OS_VALIDATED` ストリームは `MaxAge`（既定 24h）> flush+AckWait で
   構成（`ValidatedStreamLimits`）。24h 以内に復旧すれば未 flush 分も保持され、レイクへ流れ直す。
   復旧が MaxAge を超えると最古分から溢れる → その範囲はゲートウェイ側再送 or バックフィルが必要。

---

## 2. MinIO（Parquet レイク）が落ちた

**症状**: range/履歴クエリが 5xx、`ParquetLakeWriterWorker` の flush が失敗しログにリトライ。**最新値
（Hot KV）と制御は生存**（MinIO 非依存）。

**切り分け**: `GET http://<minio>:9000/minio/health/live` 応答なし。

**一次対応 / 復旧**:
1. MinIO を復旧（再起動 / ストレージ確認 / ディスク空き）。データは `minio_data`（S3 バケット `cold`）。
2. **データロスはしない設計**: writer は **MinIO 書き込み成功後にのみ JetStream を ack**
   （`ParquetLakeWriterWorker`, `AckPolicy=Explicit`）。MinIO ダウン中は ack されず、JetStream が
   AckWait 経過後に**再配信** → 復旧後にレイクへ書き込まれる（MaxAge 24h の範囲内）。
3. 読み取りは MinIO 復帰で即回復。長期の空き容量枯渇には `LAKE_RETENTION_DAYS`（ILM）と
   バックアップ退避を見直す。

---

## 3. PostgreSQL が落ちた

**症状**: ログイン後の権限判定・制御監査書き込み・`/platform/settings` などが失敗。テレメトリの
取り込み/最新値/レイク読み取り自体は PostgreSQL 非依存で継続。

**切り分け**: `pg_isready`（compose healthcheck）・pgbouncer（`building-os.pgbouncer`）の生存。

**一次対応 / 復旧**:
1. PostgreSQL を復旧（再起動 / ディスク / 接続数）。pgbouncer 経由の接続が詰まっていれば pgbouncer も再起動。
2. データ破損時は [oss-backup-restore-runbook.md](oss-backup-restore-runbook.md) §3 からリストア。
3. 復旧後、API は再接続。保留 EF マイグレーションがあれば API 再起動時に適用。

---

## 4. Keycloak が落ちた

**症状**: **新規ログイン・トークンリフレッシュ不可**。ただし発行済みアクセストークンは失効まで有効で、
API の JWT 検証は JWKS キャッシュがある限り継続しうる。

**一次対応 / 復旧**:
1. Keycloak を復旧（Pod/コンテナ、**外部 RDBMS を正本にしている本番はその DB** も確認）。
2. realm は種 `oss-stack/keycloak/realm.json`（dev）/ 外部 DB（本番）。dev のユーザーは `keycloak_data`。
3. **`DISABLE_AUTH=true` を本番の緊急回避に使わない**（認証を全開放する）。緊急時は Keycloak 復旧を優先。

---

## 5. ゲートウェイの大量切断

**症状**: 制御が 503 `gateway_offline`（#186 の fast-fail、メトリクス `control.requests{result=gateway_offline}`）、
該当ゲートウェイ配下の Point が stale/missing、operator-home / `/admin/gateways` の最終受信（last-seen,
#181②）が伸びる。

**切り分け**: `/admin/gateways` の最終受信、GatewayBridge（egress）/ ConnectorWorker（ingress, `GRPC_INGRESS_PORT`）
の到達性、ネットワーク/mTLS（[oss-gateway-security-ops.md](oss-gateway-security-ops.md)）。

**一次対応 / 復旧**:
1. 上流（ネットワーク/証明書/ingress）を復旧。ゲートウェイは egress ストリームへ再接続し、
   `GET /gateways/{id}/pointlist` を ETag 再ポーリングして復帰（#224）。
2. GatewayBridge はステートレス水平スケール（per-gateway NATS ルーティング）。再接続は同一レプリカでの
   supersede で旧ストリームを畳む（重複配信なし）。
3. 取りこぼしテレメトリはゲートウェイ側のバッファ/再送方針に依存（機器・ゲートウェイ実装による）。

---

## 6. 部分障害と縮退

- **可観測性（Prometheus/Grafana/OTLP）が落ちても本体は動く**（既定スタック外・no-op 設計）。KPI が
  null 化するだけ。
- API 障害・NATS 断時の **UI 縮退表示（degraded view）ポリシー**は未整備で、#163 の別項目
  （「障害時の UI 縮退表示ポリシー」）。現状は各画面のエラーバナー/取得失敗表示に依存。

---

## 7. 既知の制約 / 未検証

- 各障害を実機で注入した検証（ゲームデー）は未実施（本ドキュメント作成環境に Docker デーモンなし）。
  記述はコンポーネントの health 契約・ストリーム/ack 挙動・fast-fail 経路（#186）に基づく。
  **本番/ステージングで障害演習を実施し、復旧時間の目安を実測**してください。
- 復旧の**自動化**（アラート → 自動再起動、runbook automation）は本 Runbook の対象外。
- 大規模再接続時の挙動（サンダリングハード等）は大規模評価（#163）で確認する。
