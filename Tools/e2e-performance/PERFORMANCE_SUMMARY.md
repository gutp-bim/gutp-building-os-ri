# パフォーマンス・品質テスト 実施結果サマリー

OSS スタックに対して実施した E2E パフォーマンス・品質テストの累積結果。

> **最新の総合評価（2026-07-22）**: E1〜E8、#259/#260 Point List最適化、10 Building / 20 Gateway /
> 2k〜50k Pointスイープは [`docs/performance-evaluation-report.md`](../../docs/performance-evaluation-report.md)
> を正本とする。この文書の後半は過去runの時系列記録として保持する。

> **注意（アーキ移行）**: 下表の合格結果（2026-05）は**旧 TimescaleDB warm パス**に対するもの。OSS は
> その後 **Parquet レイク既定（#216）+ tail-merge（#220）** に移行済み。現行の既定アーキを測る手順は
> [`docs/oss-warm-parquet-perf-runbook.md`](../../docs/oss-warm-parquet-perf-runbook.md)（`quality_checker.py
> --mode parquet` = DuckDB で MinIO レイク検証、bridge `PARQUET_MODE`、`s2_baseline.sh MODE=parquet`）。
> Parquet経路は2026-06以降に再計測済み。直下の「Parquet経路 実測」と最新総合評価を参照。

テスト計画の詳細は [`docs/e2e-performance-quality-test-plan.md`](../../docs/e2e-performance-quality-test-plan.md) を参照。

## Parquet 経路 実測（2026-06-15、現行既定アーキ）

実 `ParquetLakeWriterWorker` → MinIO レイク → DuckDB 検証で計測（`MODE=parquet`、ローカル Docker）。
この計測の過程で本番バグ（ParquetLakeWriter の idleHeartbeat による書込不能）と既定スタックの
postgres/connector ビルド不能を発見・修正済み（PR #264）。

| シナリオ | フェーズ | 行数 | 損失 | 重複 | スキーマ不正 | 判定 |
|---|---|--:|--:|--:|--:|---|
| **S2 ベース** | baseline | 100 / 100 | 0% | 0 | 0 | ✅ |
| **S3 バースト** | burst (10s間隔) | 300 / 300 | 0% | 0 | 0 | ✅ |
| **S3 バースト** | recovery (60s間隔) | 50 / 50 | 0% | 0 | 0 | ✅ |
| **S4 品質** | baseline (5 pts/msg) | 50 / 50 | 0% | 0 | 0 | ✅ |
| **S4 品質** | wide (10 pts/msg 有効) | 100 / 100 | 0% | 0 | 0 | ✅ |

> QUICK 短縮（small scale、各 60–120s、`PARQUET_FLUSH_INTERVAL=1`）。各ランは `BUILDING_ID=run_id` で
> 専用 lake パーティションに隔離。S7（resilience）の parquet 追従は follow-up。手順は
> [`docs/oss-warm-parquet-perf-runbook.md`](../../docs/oss-warm-parquet-perf-runbook.md)。

### S5 API リードパス（Parquet 読取レイテンシ — latest + range）

> 統一エンドポイント `GET /telemetries/query`（latest + range）· 10 VU × 30s · 1,192 req · 既存 lake データ。
> 合成 point を twin に seed（`seed_twin_points.py`）して 200 を返すようにし、実 Parquet 読取を測定。

| 指標 | p(95) | 閾値 | 判定 |
|---|--:|--:|---|
| latest_value（最新値、lake フォールバック） | **59.9 ms**（med 28.7） | 500 ms | ✅ |
| range_query（warm parquet 読取） | **60.2 ms**（med 27.0） | 2,000 ms | ✅ |
| error_rate / http_req_failed | 0% | 0.1% | ✅ |

> **前提（この計測で発見・修正した本番バグ）**:
> - Parquet reader の tz バグ（PR #266）— 非 UTC ホストで warm 読取が空。
> - `MinioBlobStorage.ListAsync` の null `S3Objects`（PR 別）— 空プレフィックス list が例外 → latest フォールバックが 500。
>
> latest は本計測時 Hot KV(NATS) が未投入のため lake 最新値フォールバックを測定（KV ヒット時はさらに高速）。

### S6 ポイント制御 E2E（制御提出経路 — 初実施）

> `POST /points/{id}/control { value }` · 2 VU × 30s · 制御可能 point を twin に seed（`seed_twin_points.py
> --control-point`）。提出（202 + controlId）を測定。下流ハンドラ結果は非同期で対象外。

| 指標 | 値 | 閾値 | 判定 |
|---|--:|--:|---|
| control_submission_duration p(95) | **10.3 ms**（med 6.6） | 3,000 ms | ✅ |
| s6_error_rate（A=202 / B=404 / C=400 の検査） | 0% | 1% | ✅ |
| timeout_rate | 0% | 1% | ✅ |

> Phase A=有効提出→202+controlId、B=存在しない point→404、C=value 欠落→400。全フェーズ checks 100%。
> k6 の `http_req_failed` 閾値は除外（B/C が意図的に 4xx を返すため; 実エラーは s6_error_rate で判定）。
> egress ControlType は point の gateway binding からサーバ側で解決（既定 hono → in-process 提出）。

### S7 障害・リプレイ耐性（Parquet 追従）

> `MODE=parquet` で実施。Test A は NATS のみ（ストレージ非依存）、Test C は実 ParquetLakeWriter →
> MinIO レイクを DuckDB で検証（フェーズごと `BUILDING_ID` 隔離）。

| テスト | 結果 | 判定 |
|---|---|---|
| A: NATS JetStream replay | published 10 → replay 10（durable DeliverAll で位置0から再消費） | ✅ |
| B: 重複挙動 | parquet は read 側 dedup（`DedupById`）= DB 一意制約なし（単体テスト担保） | ✅（文書化） |
| C: bridge 強制再起動リカバリ | Phase1 50/50 loss 0% · Phase2 50/50 loss 0%（再起動後も lake へ無損失） | ✅ |

> Test C は bridge をロード中に kill→再起動し、再起動後の publish（Phase2）が lake に loss 0% で着地する
> ことを確認（NATS JetStream の at-least-once + ParquetLakeWriter durable consumer）。

---

## テストラン一覧

| 日時 (UTC) | Run ID | シナリオ | 結果 | レポート |
|---|---|---|---|---|
| 2026-05-17 22:02 | `20260517T220211Z-s5-36379` | **S5 API リードパス** | **PASS** | [report](results/20260517T220211Z-s5-36379/report.md) |
| 2026-05-18 05:47 | `20260518T054716Z-smoke-local` | Smoke（パイプライン未接続） | PARTIAL | [report](results/20260518T054716Z-smoke-local/report.md) |
| 2026-05-18 06:10 | `20260518T061044Z-smoke-22493` | Smoke（ブリッジ接続・品質チェック不一致） | FAIL | [report](results/20260518T061044Z-smoke-22493/report.md) |
| 2026-05-18 06:23 | `20260518T062323Z-smoke-24478` | **Smoke（パイプライン疎通確認）** | **PASS** | [report](results/20260518T062323Z-smoke-24478/report.md) |
| 2026-05-21 06:02 | `20260521T060207Z-s2-97362` | **S2 ベーススループット（QUICK）** | **PASS** | [report](results/20260521T060207Z-s2-97362/report.md) |
| 2026-05-21 06:07 | `20260521T060744Z-s3-97794` | **S3 バーストと背圧（QUICK）** | **PASS** | [report](results/20260521T060744Z-s3-97794/report.md) |
| 2026-05-21 06:18 | `20260521T061835Z-s4-98391` | **S4 データサイズ・スキーマ品質（QUICK）** | **PASS** | [report](results/20260521T061835Z-s4-98391/report.md) |
| 2026-05-21 06:27 | `20260521T062737Z-s7-98938` | **S7 障害・リプレイ耐性（QUICK）** | **PASS** | [report](results/20260521T062737Z-s7-98938/report.md) |
| 2026-05-21 09:32 | `20260521T093222Z-s8-3464` | **S8 UI ジャーニー（Playwright）** | **PASS** | [report](results/20260521T093222Z-s8-3464/report.md) |

---

## S2 ベーススループット — 主要指標（QUICK モード）

> Run ID: `20260521T060207Z-s2-97362` · scale=small · baseline profile · 300s

| 指標 | 値 | 閾値 | 判定 |
|------|--:|-----|------|
| DB 行数 | 250 | ≥ 250 | **PASS** |
| 損失率 | 0.0000% | ≤ 1% | **PASS** |
| 重複率 | 0 件 | ≤ 0.1% | **PASS** |
| スキーマ不正 | 0 件 | = 0 | **PASS** |

> 注: QUICK モードは small scale (10 devices) / 5 分。本番想定 (medium scale / 1 時間) は `bash Tools/e2e-performance/s2_baseline.sh` で実施。

---

## S3 バーストと背圧 — 主要指標（QUICK モード）

> Run ID: `20260521T060744Z-s3-97794` · scale=small · burst 120s → baseline 120s

| フェーズ | DB 行数 | 損失率 | 判定 |
|----------|--------:|--------|------|
| Phase 1 (burst, 10s interval) | 600 / 600 | 0.0000% | **PASS** |
| Phase 2 (recovery, 60s interval) | 100 / 100 | 0.0000% | **PASS** |

バーストフェーズ終了後、即座にベースラインに復旧。バックプレッシャによる遅延・損失なし。

---

## S4 データサイズ・スキーマ品質 — 主要指標（QUICK モード）

> Run ID: `20260521T061835Z-s4-98391` · scale=small · 120s 各フェーズ

| フェーズ | 粒度 | DB 行数 | スキーマ不正 | 損失率 | 判定 |
|----------|------|--------:|:------------:|-----:|------|
| Phase A (5 pts/msg) | baseline | 100 / 100 | 0 | 0.0000% | **PASS** |
| Phase B (10 pts/msg effective) | wide | 200 / 200 | 0 | 0.0000% | **PASS** |

大きな payload (wide: 75 pts/msg 指定、small scale でのみ 10 pts 有効) でもスキーマ不正・損失ゼロ。

---

## S5 API リードパス — 主要指標

> Run ID: `20260517T220211Z-s5-36379` · 10 VU × 約3分 · 7,561 リクエスト

| エンドポイント | p(95) | 閾値 | 判定 |
|---|---:|---:|---|
| Latest Value API | 7.71 ms | 500 ms | **PASS** |
| Range Query API | 7.70 ms | 2,000 ms | **PASS** |
| HTTP エラー率 | 0% | 0.1% | **PASS** |

閾値に対して余裕は **98%以上**。ローカル環境（WSL2 + Docker Desktop）でのベースライン。

---

## S1 Smoke — パイプライン疎通

> 最終確認 Run ID: `20260518T062323Z-smoke-24478`

| ステップ | 通過件数 / 送信件数 | 結果 |
|---|---:|---|
| MQTT publish | 100 / 100 | **PASS** |
| NATS raw | 100 / 100 | **PASS** |
| NATS validated | 100 / 100 | **PASS** |
| TimescaleDB rows | 100 / 100 | **PASS** |
| API 経由確認 | 未計測（API 未起動） | — |

---

## S8 UI ジャーニー — 主要指標

> Run ID: `20260521T093222Z-s8-3464` · 4 テスト · Playwright Chromium headless

| テスト | ロード時間 | 閾値 | 判定 |
|--------|----------:|-----:|------|
| sign-in page renders with Keycloak button | 633 ms | 3,000 ms | **PASS** |
| authenticated: dashboard loads | 2,200 ms | 3,000 ms | **PASS** |
| authenticated: navigate to buildings | 1,200 ms | 3,000 ms | **PASS** |
| admin-console: dashboard loads | 848 ms | 3,000 ms | **PASS** |

認証フロー: Keycloak direct grant (Resource Owner Password) でトークン取得 → `oidc.access_token` cookie 注入 → Next.js ミドルウェアが認証済みと判定。

---

## 現在の達成状況

| シナリオ | 状態 | 備考 |
|---|---|---|
| S1 Smoke — パイプライン | **PASS** | API 側検証は次フェーズ |
| **S2 ベーススループット** | **PASS (QUICK)** | small scale / 5 分。本番規模は手動実施要 |
| **S3 バーストと背圧** | **PASS (QUICK)** | small scale / burst 2 分 → recovery 2 分 |
| **S4 データサイズ・スキーマ品質** | **PASS (QUICK)** | baseline + wide payload、スキーマ不正 0 |
| **S5 API リードパス** | **PASS** | p(95) 7.7 ms |
| S6 ポイント制御 E2E | 未実施 | — |
| **S7 障害・リプレイ耐性** | **PASS (QUICK)** | NATS replay PASS, TimescaleDB dup 挙動記録, bridge restart Phase 2 loss=0% |
| **S8 UI ジャーニー** | **PASS** | 4 テスト全 PASS、最大ロード 2.2 s（閾値 3 s） |

---

## 既知の残課題

| 優先度 | 課題 | 対応 |
|---|---|---|
| High | S2/S3/S4 が QUICK モード（small scale 5-10 分）のみ。本番想定（medium scale 1 時間以上）は未計測 | `bash Tools/e2e-performance/s2_baseline.sh` 等を full モードで実行 |
| Medium | API 疎通確認が `api_row_count=-1`（API Server 未起動のため 404）。DB 品質は確認済み | API Server 起動後に quality_checker の API チェックを再実行 |
| Medium | S6 ポイント制御 E2E 未実施 | S5 以降のシナリオとして順次実施 |
| Low | 本番環境（クラウド/k8s）での計測なし | ローカルは参考値 |

---

## ランナースクリプト

| スクリプト | シナリオ | QUICK モード |
|------------|----------|-------------|
| `bash Tools/e2e-performance/smoke.sh` | S1 Smoke | — |
| `bash Tools/e2e-performance/s2_baseline.sh` | S2 ベーススループット | `QUICK=true bash ...` |
| `bash Tools/e2e-performance/s3_burst.sh` | S3 バーストと背圧 | `QUICK=true bash ...` |
| `bash Tools/e2e-performance/s4_quality.sh` | S4 データサイズ・スキーマ品質 | `QUICK=true bash ...` |
| `k6 run Tools/e2e-performance/k6/s5_api_read.js` | S5 API リードパス | — |
| `bash Tools/e2e-performance/s7_resilience.sh` | S7 障害・リプレイ耐性 | `QUICK=true bash ...` |
| `bash Tools/e2e-performance/s8_ui.sh` | S8 UI ジャーニー（Playwright） | — |

---

*最終更新: 2026-05-21*
