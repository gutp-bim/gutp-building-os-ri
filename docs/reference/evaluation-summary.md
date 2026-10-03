# Evaluation Summary — アーキテクチャと性能の妥当性

Building OS OSS の主要な設計判断が、実測でどう裏付けられるかをまとめます。評価フレームワーク（評価軸
E1–E8 + KPI ゲート）と最新の実測 run は [`e2e/`](../../e2e/) 配下が正本です:

- 評価計画: [`e2e/plan.md`](../../e2e/plan.md) / KPI 閾値: [`e2e/kpi-thresholds.yaml`](../../e2e/kpi-thresholds.yaml)
- 軸別手順: [`e2e/scenarios/`](../../e2e/scenarios/)
- **実測レポート（生値）**: [`e2e/evaluation-report.md`](../../e2e/evaluation-report.md)
- **総合性能レポート（スケール・再接続・Point List）**: [performance-evaluation-report.md](performance-evaluation-report.md)
- **長時間ソーク**: [E10 scenario](../../e2e/scenarios/E10-endurance-soak.md)（73h）/
  [実ゲートウェイ mTLS 接続レポート](gateway-mtls-endurance-soak-report.md)（約 73.6h）

> E1–E8の基準run（2026-06-16）は単一ホスト・single building・smallスケールです。その後、2026-07-22の#261で
> 10 Building / 20 Gateway / 最大50,000 Pointまで拡張し、2026-09には73時間ソーク（E10）と実ゲートウェイの
> mTLS長時間接続評価を実施しました。絶対値は環境依存であり、論文用の確定値は
> 専用ベンチ機・largeスケールでの再計測を推奨します。総合結果は
> [performance-evaluation-report.md](performance-evaluation-report.md)を参照してください。

---

## 1. 評価フレームワーク（再現可能なゲート）

`run-all.sh` が評価軸を実行 → 各軸の native 出力を正準 `{axis, metrics}` JSON に正規化 →
`gate.py` が `kpi-thresholds.yaml` と突合し `kpi-report.md` / `gate.json` を生成します。比較可能な KPI が
1 つでも閾値外なら非ゼロ終了（CI/論文ゲート）。これにより**主張＝機械可読な合否**として再現できます。

```bash
docker compose -f docker-compose.oss.yaml up -d
bash e2e/runner/run-all.sh        # → e2e/results/<run-id>/kpi-report.md, gate.json
```

---

## 2. ヘッドライン結果（finalgate-20260616, E1–E8 通し）

> 2026-06-16 時点の基準 run。以降の追加評価は §2-2。

| ⭐ 指標 | 実測 | 閾値 | 何を示すか |
|---|--:|--:|---|
| E2 ingest E2E p95（gen→validated） | **2.9 ms** | < 2,000 ms | gRPC 直送取り込みの低遅延 |
| E3 latest API p95 | **6.9 ms** | < 500 ms | Hot KV による最新値の高速応答 |
| E4 warm 24h range p95 | **54.7 ms** | < 2,000 ms | Parquet レイクの直近レンジ読取 |
| E5 point resolution success | **1.000** | ≥ 0.999 | `(gateway_id, point_id)` 契約の正しさ |
| E6 stale-replay count | **0** | == 0 | deadline-bounded 制御の安全性 |

ゲート総合: **20 PASS / 1 FAIL / 8 SKIP**（FAIL は E4 agg_hour で、後述の通り**修正済み**）。詳細値は
[`e2e/evaluation-report.md`](../../e2e/evaluation-report.md)。

## 2-2. 追加評価のヘッドライン（E1–E8 以降）

| 評価 | 実測 | 何を示すか |
|---|--:|---|
| #261 スケール（10 Building / 20 Gateway / 50,000 Point） | ingress **50,000/50,000** 受理・Lake 損失 **0%**、Point List 最大 **2,745 ms**（予算 5,000 ms） | 50k Point まで整合性と性能予算を満たす |
| #262 Gateway 100 本の集中再接続 | **2,693 ms** で全接続が制御可能、control / ingress 各 **100/100**、損失・重複 0 | 再接続ストームへの耐性 |
| #259/#260 Point List 最適化（10k Point） | OxiGraph query **26.6 s → 91 ms（291×）**、ETag 一致 304 は **0.39 ms / 追加 query 0** | Gateway 所有 Point の絞り込みと ETag の NATS KV 共有 |
| E10 長時間ソーク **73h**（2026-09-26〜29） | 1,633,743 frame、loss / dup / 再起動 / OOM **すべて 0**、全 10 gate PASS | 24h retention 到達後に ConnectorWorker・NATS の RSS が定常化（24–73h スロープ −0.12 / +0.01 MiB/h）、リーク兆候なし |
| 実ゲートウェイ mTLS 接続 約 **73.6h** | ConnectorWorker 再起動 RTO 13–16 s、ネットワーク分断 RTO 48–78 s、MinIO 停止 RTO 約 25 s、観測範囲で損失なし | 実機・mTLS 終端 nginx 経由での運用適合性（Control 系の障害注入は部分完了） |
| `WORKER_ROLE` 分割（#400） | all は変更前と同等（ingest p95 2.9→3.1 ms）、分割 3 プロセスも動作確認。スループット改善はなし、メモリは約 3 倍 | 「分割しても壊れない」ことの確認（効果検証は E12） |

詳細と条件・限界は各レポートを参照。E11（Lake 保持・compaction の大規模継続負荷）/ E12（compaction 中の
tail latency と `WORKER_ROLE` 判定）は capped-run のシナリオとして整備済みで、個別実行（`ONLY` 既定外）です。

---

## 3. 設計判断ごとの妥当性

### 3-1. テレメトリは Store-and-Forward、Parquet レイクが既定（#216）
- **E1 ingest throughput**: gRPC 持続負荷 6,000 frames → lake 6,000 行、**loss 0 / dup 0 / invalid 0 /
  sustained ratio 1.0**。欠落・重複なく取り込めることを実証。
- **E2 ingest latency**: gen→validated **p95 2.9ms**。gRPC ingress が validated へ直送（per-protocol
  connector ホップなし）であることの効果。
- **E7 storage cost**: parquet **2.84 B/行** vs TimescaleDB 非圧縮 **134.8 B/行** → **比 0.02（約 47× 小）**。
  圧縮済み TimescaleDB との推定比でも parquet が概ね 2.5–5× 小（[evaluation-report 参照](../../e2e/evaluation-report.md)）。
  オブジェクトストレージ + オープン形式 + DB 圧縮計算不要、という運用利点も含め、**既定を Parquet レイクに
  した判断**を裏付け。

### 3-2. Hot/Warm/Cold 階層 + 統一読み取り（#214/#215/#220）
- **E3 latest / freshness**: latest API **p95 6.9ms**、event→Hot KV 反映 **13ms**、stale 率 **0**。最新値は
  Hot KV、履歴はレイク、という階層分離が機能。
- **E4 historical**: warm 24h **54.7ms** / cold 7d **75.4ms** / agg_day cache-hit **9ms** /
  multipoint_scaling **0.11（sublinear）**。tier 自動選択（`/telemetries/query`）と 1 スキャン multi-point の
  有効性。
  - **agg_hour（集計）**: 当初 aggregate-on-read コールドが bimodal（〜5.3s）だったが、真因は **rollup
    （agg_hourly）未生成時間のフォールバック**。compaction で settled hour の rollup を生成する**本番経路**で
    測ると **606ms で安定**（閾値 3s 内）。「事前 rollup + 欠落のみ on-read」という階層設計の妥当性を確認。
- **#220 tail-merge 修正**: 直近窓 warm が 3,000ms→数十 ms に改善（JetStream 末尾の取りこぼし回避）。

### 3-3. `(gateway_id, point_id)` 共有 point list 契約（#181/#224）— 本評価の独自性
- **E5 pointlist integrity**: resolution **1.000** / unknown 拒否 **1.0** / ownership 拒否 **1.0** /
  remap 正当性 **1.0** / twin lookup p95 **11ms**。プロトコル差異を隠蔽しつつ、未知・非所有の送信を確実に
  拒否し、再マッピングが正しく反映されることを実証。契約境界の健全性は本アーキテクチャの核。

### 3-4. 制御は deadline-bounded な request-reply（#186）
- **E6 control safety**: **stale-replay 0** / typed-failure 分類 **1.0** / not-writable 拒否 **1.0** /
  RTT p95 **22.8ms**。制御はエフェメラルな request-reply（耐久キューではない）ため、障害中の古いコマンドが
  復旧後に**勝手に実行されない**（stale-replay=0）。テレメトリの耐障害性（store-and-forward）と物理制御の
  安全性を両立。
  - 局所スタックで再現困難な offline→503 / duplicate-write / success は backend unit test + #186 で担保
    （[evaluation-report 参照](../../e2e/evaluation-report.md)）。

### 3-5. 障害復旧（#246）
- **E8 resilience**: connector 停止→再起動で **RTO 4.5s**、**復旧後データ損失 0**（store-and-forward /
  再接続後 publish の非損失）。部分障害からの回復性を実証。
- **Gateway 集中再接続（#262）**: 100 Gateway が 500 ms jitter で同時再接続し、2.7 s で全接続が制御可能。
- **実ゲートウェイ（mTLS, 約 73.6h）**: ConnectorWorker 再起動・ネットワーク分断・MinIO 一時停止から
  観測範囲で損失なく復旧（RTO 13–78 s）。

### 3-6. 長時間安定性（E10, #297）
- **73h 単一ストリーム**: 1,633,743 frame で loss / duplicate / 再起動 / OOM すべて 0、10 gate 全 PASS。
  24h 時点では収束していなかった ConnectorWorker RSS は、JetStream の 24h retention 到達後に定常化
  （24–73h スロープ −0.12 MiB/h、gen2 は 2.6 MiB 前後で平坦）。
- 留意: RustFS 上・`mem_limit` なしの 1 本のみ。`RSS − GC committed` の gate 値（+2.45）は閾値 3.0 に近い。

---

## 4. 既知の限界

- E1–E8基準runはsingle building / smallスケールだが、#261で10 Building / 20 Gateway / 50,000 Pointまで
  別途PASS。ただし50k Pointからの長時間継続送信・同時API read混在負荷は未評価。
- Point List は 10k→50k で 170→2,745 ms と非線形に増加。100k 診断（共有サンドボックス, 非公式）では
  API 応答 47 s のうち OxiGraph query は約 9% に過ぎず、残りの内訳は未特定（専用ホストでの再計測が必要）。
- 長時間ソークは 73h・単一ホスト・`mem_limit` なし。実ゲートウェイ評価は 1 棟・1 台で、Control 安全性の
  障害注入は部分完了、確認は累積件数ベース（sequence / point 単位の突合ではない）。
- E6 の offline_503 / success_rate / duplicate_write、E8 の graceful-degradation マトリクスは、切断 GW・接続 GW・
  個別サービス停止など**別前提**が要るため gate では SKIP（設計と unit test で担保）。
- 集計コールド 30 日日次（参考 ~10s）は rollup 生成（compaction）の進行度に依存。

---

## 5. 関連

- フレームワーク: [`e2e/README.md`](../../e2e/README.md) / 計画: [`e2e/plan.md`](../../e2e/plan.md)
- 生実測: [`e2e/evaluation-report.md`](../../e2e/evaluation-report.md)
- アーキテクチャ: [system-architecture.md](../architecture/system-architecture.md) / [oss-tier-architecture.md](../architecture/oss-tier-architecture.md)
- 総合性能レポート: [performance-evaluation-report.md](performance-evaluation-report.md)
- 長時間ソーク: [E10](../../e2e/scenarios/E10-endurance-soak.md) / [mTLS 実ゲートウェイ](gateway-mtls-endurance-soak-report.md)
- ロール分割計測: [worker-role-split-measurement.md](worker-role-split-measurement.md)
- ゲートウェイ契約: [gateway-integration.md](../guides/gateway-integration.md)
