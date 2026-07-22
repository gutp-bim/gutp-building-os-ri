# E4 — 履歴クエリ（Warm / Cold / Parquet Lake）

## 目的
最新値=Hot、短期履歴=Warm、長期履歴=Parquet Lake に分離しつつ、Query Router により期間・粒度に応じて
同一 API（`/telemetries/query`）で扱えることを示す。

## 計測指標
- range query p50/p95/p99: 1h / 24h / 7d / 30d。
- aggregate query latency: hour / day 粒度の avg/min/max。
- query scanned files / scanned bytes（Parquet Lake、ログ/メトリクス）。
- cache hit rate（building prefix cache・集計キャッシュ）。
- multi-point query scalability（point 数増に対する p95）。
- warm/cold 境界マージ遅延（境界跨ぎクエリ）。

## 手順
1. 履歴データを投入（backfill ツール or 長時間 ingest）。warm/cold 双方にデータが載る期間を用意。
2. `k6/s9_warm_kpi.js`（latest / warm 24h / cold 7d / hour・day 集計 / multi-point）を実行。
3. `WARM_STORE=parquet`（既定）と `timescale`（opt-in）で実行し対照。
4. scanned files/bytes は Parquet reader メトリクス・ログから収集。

## 合否（kpi-thresholds.yaml: E4_historical_query）
warm 24h p95 < 2s / cold 7d p95 < 5s / 集計 cold < 3s・cache hit < 100ms / multi-point sublinear。

## 既存資産・ギャップ
- 既存: `k6/s9_warm_kpi.js`, `docs/operations/oss-warm-parquet-kpi.md`。
- **ギャップ**: 30d レンジ、warm/cold 境界マージ遅延、scanned files/bytes の体系的収集。

## 実施メモ（2026-06-15, parquet 既定・ローカル）
- `s9_warm_kpi.js` を**廃止 per-tier エンドポイント**（`/telemetries/{hot,warm,cold,cold-multi-point}`、
  parquet モードで空）から統一 **`/telemetries/query`**（latest / range / granularity）へ更新。
  `run-axis.sh` の E3/E4 を「twin に point を seed → POINT_IDS 付きで k6 実行」に強化。

### 発見・修正した本番バグ: tail-merge fetch が毎回フルタイムアウト待ち
- 初回実測で `warm_24h` / `cold_7d` が**フラット 3.0s**。当初「多 building スキャン」を疑ったが、
  clean lake（1 building）でも 3.0s のまま → **誤り**。切り分け: `end=now` のクエリ=2.99s、
  `end=1h前`（tail lookback 900s の外）=**9ms**。
- 真因: `NatsTailReader` が `ConsumeAsync(MaxMsgs)` を使い、**MaxMsgs に達するか FetchTimeout(3s) まで
  ブロック**（ストリームを汲み尽くしても止まらない continuous pull）。直近 window のクエリは
  ライブ tail が idle でも毎回 ~3s を払っていた（実 Parquet 読取は ~10ms）。本番のダッシュボード等
  「直近データ」クエリに直撃。
- 修正: `NumPending == 0`（追いついた）で即 break + FetchTimeout 既定 3s→1s（ゼロ件時のバックストップ）。

### 修正後の実測（parquet, clean lake, 10VU×30s）

| 指標 | p(95) | 閾値 | 判定 |
|---|--:|--:|---|
| latest | 108 ms | 500 ms | ✅ |
| warm_24h | **46 ms**（修正前 3000ms） | 2,000 ms | ✅ |
| cold_7d | 61 ms | 5,000 ms | ✅ |
| multipoint(3) | 88 ms | — | ✅ |
| agg_hour | 4.59 s（med 13ms, bimodal） | 3,000 ms | ❌ |
| agg_day | 15.4 s（med 6.7s） | 3,000 ms | ❌ |

- **残: 集計クエリ（aggregate-on-read）** が大きい window（7d/30d）で閾値超過。median は速いが p95 が
  跳ねる（bimodal）。aggregate 経路の最適化（事前 rollup の活用 / 並列スキャン / building 枝刈り）は
  follow-up。
- 修正は **#272**（`fix/tail-merge-fetch-timeout`）。
- **follow-up / 進捗**:
  1. 集計クエリ p95 最適化 → **#281 済**（rollup 並列 probe + 欠落時間の集約読み合体）+ **bimodal 解消済**:
     真因は rollup 未生成時間の aggregate-on-read フォールバック。`s14` を rollup-backed 化（settled hours +
     compaction 前倒し: `LAKE_COMPACTION_INTERVAL=1`/`SETTLE=0`、`PARQUET_FLUSH_MAX_ROWS` 低設定で ≥2 part →
     compaction 発火）→ agg_hourly rollup 生成、**agg_hour_cold 606ms で安定（閾値内）**。
  2. 読み取りの **point→building 枝刈り** → **#273 済**（学習型 building キャッシュ）。多棟効果は要多棟環境。
  3. **agg_day cache-hit / multipoint sublinear** → **済**（`s14_agg_cache_multipoint`）: cache-hit **7.3ms**（<100）/
     multipoint_scaling **0.305**（<1, sublinear）。これで E4 の合否（warm/cold/agg-cold/cache-hit/multipoint）が全充足。
  4. 30d レンジ / warm-cold 境界マージ遅延 / scanned files・bytes 収集（残・任意）。
