# E8 — 障害復旧・可用性（推奨）

## 目的
Building OS と gateway を含む全体で、部分障害時に Hot/Warm/Cold が graceful degradation し、復旧後に
欠損なく回復できることを示す。

## 障害シナリオと指標
| シナリオ | 主な指標 |
|----------|----------|
| Building OS 停止 | gateway buffer depth, dropped frames, recovery time |
| NATS 停止 | ingest 停止時間, 復旧後の再接続時間 |
| MinIO 停止 | Parquet write failure count, retry count, health degradation |
| API Server 停止 | query unavailable duration |
| gateway 停止 | missed telemetry, reconnect time |
| connector 停止 | no_connector ratio, telemetry gap |
| Point List 不整合 | unresolved count, accepted/sent drift |

## 重要指標
- recovery time objective（RTO）実測。
- data loss under outage（障害中の欠損率、復旧後）。
- backlog drain time（復旧後に滞留が解消するまで）。
- graceful degradation: Hot/Warm/Cold の一部障害時に代替経路が機能するか。

## 手順
1. 定常負荷下で各コンポーネントを順に停止/再起動（fault injection）。
2. 停止中の gateway buffer・dropped・NATS backlog を記録。
3. 復旧後の再配信で行数不変（損失ゼロ）と drain time を確認（`s7_resilience_test.py` の
   durable consumer replay / bridge restart recovery を拡張）。

## 合否（kpi-thresholds.yaml: E8_resilience）
data loss under outage ≤ 1% / RTO・backlog drain は report / graceful degradation は可否を記録。

## 既存資産・ギャップ
- 既存: `s7_resilience.sh`, `s7_resilience_test.py`（NATS replay / dup insert / bridge restart）。
- **ギャップ**: MinIO/API/gateway/connector 個別停止、RTO 実測、graceful degradation の体系化。
