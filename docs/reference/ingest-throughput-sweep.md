# gRPC ingest スループット sweep（S22）

実施日: 2026-11-06 / 対象: `gRPC GatewayIngress → NATS JetStream → ParquetLakeWriter → RustFS` /
ハーネス: [`Tools/e2e-performance/s22_ingest_rate_sweep.py`](../../Tools/e2e-performance/s22_ingest_rate_sweep.py) /
生データ: [`ingest-throughput-sweep-data/`](ingest-throughput-sweep-data/)

既存の評価（[performance-evaluation-report.md](performance-evaluation-report.md)）は正確性と規模（最大 50,000 Point、
各 1 frame）を示したが、**継続レートの上限は未計測**だった（同 §7 項目 4）。本書はそのギャップを埋める最初の計測。

## 1. 結論

- **損失なしで維持できた最大レート: 約 2,000 frame/s**（3 stream・3 gateway、60 秒、損失 0・重複 0・JetStream 滞留ほぼ 0）。
  単一 stream では **約 1,000 frame/s が PASS、約 1,600 frame/s が頭打ち**。
- **限界を超えても損失は出ない。** 頭打ちの段階でも Lake 行数は受理数と一致し（損失 0）、JetStream の滞留も 0 だった。
  過負荷は「データ欠落」ではなく「送信側が受理を待たされて送れる量が減る」形（背圧）で現れる。
- 3 stream で約 3.3k、6 stream で約 4.3k frame/s まで受理した。stream を増やしても線形には伸びない（§4）。
- 頭打ちの時点で Worker は約 1.2〜1.3 コア、NATS コンテナは約 0.7〜0.85 コアで、いずれも 1 コアを超えていない。
  ホスト全体は約 70%（4 コア中 2.8）。**どの部品が律速かは、この共有 4 コア環境では断定できない**（§4）。

## 2. 結果

各段階 60 秒（診断のみ 40 秒）。判定は「達成率 ≥95%・損失 ≤1%・重複 ≤0.5%・終了時滞留 ≤ 5 秒分」。

| run | stream | 目標/s | 実測/s | 達成率 | 損失 | Worker CPU p50 | 判定 |
|---|--:|--:|--:|--:|--:|--:|---|
| 1 | 1 | 500 | 499.8 | 100% | 0 | 57% | PASS |
| 1 | 1 | 1,000 | 999.8 | 100% | 0 | 89% | PASS |
| 1 | 1 | 2,000 | 1,582.7 | 79% | 0 | 114% | FAIL |
| 2 | 3 | 2,000 | 1,999.2 | 100% | 0 | 108% | PASS |
| 2 | 3 | 5,000 | 3,241.1 | 65% | 0 | 128% | FAIL |
| 診断 | 3 | 5,000 | 3,291.6 | 66% | 0 | 126% | FAIL |
| 診断 | 6 | 5,000 | 4,299.7 | 86% | 0 | 116% | FAIL |

「実測/s」は受理された frame の持続レート。FAIL の行はすべて達成率のみが理由で、損失・重複・滞留の基準は満たしている。
Lake 行数が受理数より数件多いのは、点の登録確認用に送る探査フレームが Lake に入るため（損失ではない）。

## 3. なぜ stream あたりに上限があるのか（コード上の事実）

`NatsIngressTelemetryBus.PublishAsync` は 1 frame ごとに次の 2 つを**直列に待つ**。

1. JetStream への publish と ack（永続化されてから受理とする設計。#187）
2. Hot KV への書き込み（`ValidatedTelemetryHotStore.WriteAsync`）

したがって 1 本の client-stream のレートは、NATS との往復 2 回ぶんの時間で決まる。耐久性（ack 後に受理）を優先した
意図的な設計であり、欠陥ではない。実測の単一 stream 上限（約 1,600/s）は、この往復時間と整合する。

## 4. 未確定の点（正直に）

- 3 stream → 6 stream で 3.3k → 4.3k と伸びが鈍い。**NATS（JetStream は 1 stream への書込みが直列）か、Worker の
  共有部分か、ホスト CPU の取り合いか**は分離できていない。負荷生成器（Python 3〜6 プロセス）・Worker・NATS・RustFS が
  同じ 4 コアを使っているため。
- 6 stream の段階ではホスト CPU 約 284%（4 コア中）で、負荷生成器の CPU がボトルネックに混ざっている可能性がある。
- 3 stream 5,000/s と 6 stream 5,000/s の NATS CPU は 0.7〜0.85 コアで、1 コア未満。NATS が単スレッド的に律速している
  可能性はあるが、この計測だけでは確定しない。
- 各段階の drain 時間（約 60 秒）は `PARQUET_FLUSH_INTERVAL=1`（分）で量子化されており、遅延ではなく flush 周期を示す。

## 5. 測定環境と限界

- クラウドのコンテナ（4 vCPU / 15 GiB）。Worker は Docker ビルドが NuGet に届かないため **ホストで `dotnet publish` した
  Release 版を直接実行**し、NATS・OxiGraph・RustFS のみ Docker で動かした。
- OxiGraph は ghcr.io が取得できないため Docker Hub の `oxigraph/oxigraph:latest`（0.5.13）で代用（compose は 0.5.0）。
- `quality_checker.py`（DuckDB httpfs）は拡張のダウンロードが遮断されるため使わず、boto3 + ローカル DuckDB でレイクを数えた。
- 負荷生成器とサーバーが同一ホストで CPU を取り合う。絶対値は専用ホストの容量保証にならない。
- 各段階 60 秒の短時間計測。長時間の安定性（メモリ増加・compaction 影響）は未確認。Worker の RSS は 650 MB → 1.25 GB と
  レートに応じて増えた（頭打ちの段階で最大 1.26 GB）。
- 結果が示せるのは「この環境では 2,000 frame/s までは余裕を持って維持できる」ことまで。

## 6. 目標スループットの提案

目標は Point 数と送信周期から決める（frame/s ≒ Point 数 ÷ 送信周期[秒]）。

| 想定 | 例 | 必要レート |
|---|---|--:|
| 中規模ビル 1 棟 | 5,000 Point / 60 秒周期 | 約 83/s |
| 複数ビル | 50,000 Point / 60 秒周期（S17 の最大規模） | 約 833/s |
| 準リアルタイム | 50,000 Point / 10 秒周期 | 約 5,000/s |

提案する段階目標（運用は限界の 50% 以下を目安にする）:

1. **基準目標: 1,000 frame/s 持続**（約 50,000 Point を 60 秒周期）。単一 stream で既に PASS しており、限界（約 2,000/s 以上）の
   約半分で、余裕を残せる。まずこれを正式な SLO 候補とする。
2. **拡張目標: 2,000 frame/s 持続**（約 10 万 Point を 60 秒周期）。3 stream で PASS。gateway を複数に分けて接続する前提。
3. **挑戦目標: 5,000 frame/s 持続**（50,000 Point を 10 秒周期）。この環境では未達。専用ホストでの再計測と、
   NATS・Worker の律速箇所の特定が前提。

## 7. 次にやること（優先順）

1. **専用ホスト（または負荷生成器を別ノード）で再計測する。** 共有 CPU の影響を除き、律速箇所を確定する。
2. **律速箇所の特定:** NATS（JetStream の stream 設定・ストレージ・`js.PublishAsync` の並列度）と Worker（Hot KV 書込みの
   直列待ち）を個別に計測する。Hot KV の書込みを ack 待ちの外に出せるかは設計判断が要る（#187 の耐久性と両立するか）。
3. **目標の 70% で 1〜4 時間の持続試験**を行い、メモリ増加（RSS）・compaction・Lake の小ファイル増加を確認する。
4. **Point 数・frame あたりの点数・gateway 数**を振り、50k Point 維持下の継続送信（既存レポート §7 項目 4）を測る。
5. Kubernetes 上で ingest を複数 replica にした場合（`WORKER_ROLE=ingest` の水平スケール）の線形性を確認する。

## 8. 再現方法

```bash
# スタック（Docker 内ビルドが使えない環境ではインフラのみ Docker、Worker はホストで実行）
docker compose -f docker-compose.oss.yaml up -d building-os.nats building-os.oxigraph building-os.minio
dotnet publish DotNet/BuildingOS.ConnectorWorker -c Release -o /tmp/cw
GRPC_INGRESS_PORT=5051 PARQUET_FLUSH_INTERVAL=1 NATS_URL=nats://localhost:4222 MINIO_ENDPOINT=http://localhost:9000 \
  OXIGRAPH_ENDPOINT=http://localhost:7878 WARM_STORE=parquet ENABLE_SIM_CONTROL=true dotnet /tmp/cw/BuildingOS.ConnectorWorker.dll

# sweep（stream 数は --streams で固定、既定は自動）
cd Tools/e2e-performance
python s22_ingest_rate_sweep.py --out results/S22 --rates 500,1000,2000,5000 --streams 3 --duration 60
```
