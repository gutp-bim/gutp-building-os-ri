# gRPC ingest スループット sweep（S22）

実施日: 2026-11-06 / 対象: `gRPC GatewayIngress → NATS JetStream → ParquetLakeWriter → RustFS` /
ハーネス: [`Tools/e2e-performance/s22_ingest_rate_sweep.py`](../../Tools/e2e-performance/s22_ingest_rate_sweep.py) /
生データ: [`ingest-throughput-sweep-data/`](ingest-throughput-sweep-data/)

既存の評価（[performance-evaluation-report.md](performance-evaluation-report.md)）は正確性と規模（最大 50,000 Point、
各 1 frame）を示したが、**継続レートの上限は未計測**だった（同 §7 項目 4）。本書はそのギャップを埋める最初の計測。

## 1. 結論

- **単一 stream の上限は約 1.5〜1.6k frame/s**（1,000/s は PASS、2,000/s 目標では 1,583/s）。stream 数に応じて伸び、
  3 stream で約 3.3k、12 stream で約 5.1k、**24 stream で約 8.8k frame/s を損失 0・重複 0 で受理**した（共有 4 コア環境、
  1 Worker）。目標レートの 95% 以上を維持できた最大の段階は 3 stream の 2,000/s だった。
- **限界を超えても損失は出ない。** 頭打ちの段階でも Lake 行数は受理数と一致し（損失 0）、JetStream の滞留も 0 だった。
  過負荷は「データ欠落」ではなく「送信側が受理を待たされて送れる量が減る」形（背圧）で現れる。
- **律速は CPU ではなく、1 frame ごとの NATS 往復の直列待ち。** stream を 1→24 に増やすと受理レートは約 1.6k→8.8k
  frame/s に伸びる一方、Worker の CPU は約 1.3 コアでほぼ一定（§4）。NATS サーバー自体はパイプライン化すれば約 25k/s
  を捌く。
- **1 本の gRPC stream は 1 つの Worker に固定されるため、Worker を増やしても 1 stream の上限（約 1.5k frame/s）は
  上がらない。** 高レートの gateway は複数 stream に分ける必要がある（§4、§7）。

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

## 4. 切り分け（第 2 回計測）

Worker を介さない NATS 単体のベンチ（`ingest-throughput-sweep-data/nats_bench.py`、同形式のペイロード、各 8 秒）と、
stream 数・Worker 数を変えた sweep（目標 10,000/s、40 秒、負荷生成器 2 プロセス）で切り分けた。

**NATS 単体（Worker なし、Python 1 プロセス）**

| 操作 | 並列 | ops/s | p50 |
|---|--:|--:|--:|
| publish-ack のみ | 1 | 2,866 | 0.31 ms |
| publish-ack + KV put（Worker と同じ操作） | 1 | 1,515 | 0.60 ms |
| publish-ack + KV put | 3 | 2,459 | 1.16 ms |
| publish-ack + KV put | 6 | 3,550 | 1.61 ms |
| publish-ack + KV put | 24 | 7,605 | 3.00 ms |
| publish のみ（パイプライン、window 200） | 1 | 24,950 | — |

**Worker 経由（1 Worker）の stream 数スケーリング**

| stream | 実測 frame/s | Worker CPU p50 | ホスト CPU p50 |
|--:|--:|--:|--:|
| 1 | 1,583 | 114% | 227% |
| 3 | 3,292 | 126% | 274% |
| 6 | 4,300 | 116% | 284% |
| 12 | 5,109 | 133% | 271% |
| 24 | 8,810 | 127% | 289% |

**Worker 2 プロセス（`all` + `ingest`、各 6 stream）**: 6,100 frame/s（1 Worker・12 stream の 5,109 に対し +19%）。
このとき ParquetLakeWriter の JetStream 滞留は 1 Worker の 12〜24 stream で最大 1.5k〜2.3k 件、2 Worker では 4 件だった。

読み取れること:

1. **NATS 単体の「publish-ack + KV put」逐次ループが、Worker の単一 stream 上限（約 1.5〜1.6k/s）をほぼ再現する。**
   Worker の処理自体は主因ではない。
2. **Worker の CPU は stream 数に依らず約 1.3 コアで一定**なのに、レートは stream 数に比例して伸びる。
   → CPU 律速ではなく、NATS の応答待ち（レイテンシ律速）である。並列度を上げた分だけ待ち時間を重ねられている。
3. **NATS サーバーは余力がある**（パイプライン publish で約 25k/s、24 並列の ack 付き publish で約 17k/s）。
   ただし並列度を上げると 1 操作あたりの遅延が伸びる（p50 0.6→3.0 ms）。
4. **Worker 2 プロセスの効果は限定的**（+19%）。ホスト CPU が 4 コア中 3.1 コア相当まで埋まっており、
   この共有ホストではレプリカ増による伸びを正しく測れない。**「Worker が水平スケールするか」は専用ホストでの確認が必要。**
5. 1 本の stream を処理するのは 1 つの Worker なので、**単一 gateway が 1 本の stream で送る限り、レプリカを増やしても
   その gateway の上限は上がらない**（上のコードパスは stream 内で逐次）。

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
3. **挑戦目標: 5,000 frame/s 持続**（50,000 Point を 10 秒周期）。この環境でも 12 stream 以上に分ければ到達できた
   （5,109/s）が、その分の stream を gateway 側で分割できることが前提。逐次待ちの短縮（§7 項目 2）で 1 stream 当たりを上げる。

## 7. 次にやること（優先順）

> 設計課題は #625（1 stream の直列待ち）、専用ホストでのスケール検証は #626 で追跡する。

1. **専用ホスト（または負荷生成器を別ノード）で再計測する。** 共有 CPU の影響を除き、律速箇所を確定する。
2. **逐次待ちの短縮（設計課題）:** Hot KV の書込みを frame ごとの待ちから外す（点ごとの最新値に合体して定期書込み）、
   JetStream publish を有界ウィンドウでパイプライン化して StreamAck の前に ack を揃える、複数 frame を 1 メッセージ
   （既存の `telemetries` 配列）にまとめる、など。#187（ack 後に受理）の耐久性を保ったまま効果を測る。
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
