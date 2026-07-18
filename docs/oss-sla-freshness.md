# SLA / 鮮度モデル — テレメトリの「いつ読めるか」

Building OS のテレメトリ読み取りは Hot / Warm / Cold の 3 層を `GET /telemetries/query` が自動選択します
（[oss-tier-architecture.md](oss-tier-architecture.md)）。層によって**鮮度（イベント発生から読めるまでの遅延）**
が異なるため、運用者・利用者が「どのクエリは即時で、どのクエリは何分遅れるか」を判断できるよう本書にまとめます。

> ⚠️ 本ソフトウェアは現状有姿（AS IS）・無保証です。下記は**設計上の鮮度モデルと目標値**であり、契約上の
> SLA を保証するものではありません（[免責事項](../README.md#免責事項-disclaimer)）。実値は環境・負荷依存で、
> 本番スケールの確定は専用ベンチでの実測（[#297](https://github.com/takashikasuya/gutp-building-os-oss/issues/297)）が前提です。

---

## 1. 一目でわかる鮮度モデル

| 読み取り | 経路 | 鮮度（イベント→読める） | 目標 p95 レイテンシ | 用途 |
|---|---|---|---|---|
| **最新値** `latest=true` | Hot KV（NATS KV `telemetry-latest`） | **ほぼ即時**（publish 直後に上書き） | < 20ms（実測 latest API p95 ~7–60ms[^m]） | リアルタイムダッシュボード |
| **直近レンジ**（end が現在に近い） | Warm（Parquet レイク）＋ **tail-merge**（#220） | flush 未到達分を JetStream から補完し**ほぼ即時** | < 2,000ms（実測 warm p95 ~55ms） | 直近のチャート末尾 |
| **過去レンジ**（end が flush 済み区間） | Warm/Cold（Parquet レイク） | **flush 間隔ぶん遅延**（既定 5 分） | < 2,000ms（warm）/ < 5,000ms（cold） | 履歴チャート |
| **集計**（Hour/Day 粒度） | rollup（agg_hourly）＋欠落のみ on-read | rollup 生成（compaction）の進行度に依存 | < 3,000ms（実測 agg_hour ~606ms, rollup-backed） | 長期トレンド |

[^m]: 計測条件（ローカル/QUICK）により latest API p95 は 7ms（E3 freshness 専用ハーネス）〜60ms（S5 エンドポイント全体）。
  いずれも目標を大きく下回る。生値は [PERFORMANCE_SUMMARY](../Tools/e2e-performance/PERFORMANCE_SUMMARY.md) / [evaluation-summary.md](evaluation-summary.md)。

**要点**: 「最新値」と「直近レンジ末尾」は即時。「過去レンジ（flush 済み区間）」だけが flush 間隔ぶん遅れます。

---

## 2. なぜ層で鮮度が違うのか

```
ゲートウェイ → NATS validated.telemetry ──┬─► Hot KV（最新1件を上書き）         … 即時
                                          └─► ParquetLakeWriter ─(flush)─► MinIO レイク … flush 間隔ぶん遅延
```

- **Hot KV（最新値）**: `validated.telemetry` を受けた瞬間に `point_id` ごとの最新 1 件を上書き。永続化バッチを
  待たないので即時。ただし保持は最新 1 件のみ（履歴は持たない）。
- **Parquet レイク（履歴）**: `ParquetLakeWriterWorker` が行をバッファし、`PARQUET_FLUSH_INTERVAL`（既定 **5 分**）
  または `PARQUET_FLUSH_MAX_ROWS`（既定 50,000 行）に達した時点で MinIO へ Parquet ファイルを書き出す。
  書き出されるまでの末尾データはレイクに存在しない＝過去レンジクエリでは flush 間隔ぶんの空白が生じる。

つまり**鮮度と書き込みコストのトレードオフ**を flush 間隔で調整します。短くすると鮮度↑・小ファイル増加（compaction
負荷↑）、長くすると鮮度↓・書き込み効率↑。

---

## 3. tail-merge — 直近レンジの空白をゼロにする（#220）

flush 前の末尾が「過去レンジクエリ」に空くと、直近のチャート末尾が欠けて見えます。これを埋めるのが **tail-merge** です
（[oss-parquet-tail-merge.md](oss-parquet-tail-merge.md)）。

- warm クエリの `end` が直近（`now - PARQUET_TAIL_LOOKBACK_SEC` 以内、既定 900s）のとき、レイクのスキャンと**並行**に
  JetStream の ephemeral ordered consumer で未 flush 分を読み、レイク結果に**欠けている id のみ**マージ。
- フェッチ失敗時は**レイクのみの結果へデグレード**（可用性優先、200 返却）。クエリレイテンシ追加は p95 < 500ms 目標。

### tail-merge の有無で何が変わるか

| | tail-merge **無効**（`PARQUET_TAIL_MERGE_ENABLED=false`） | tail-merge **有効**（既定） |
|---|---|---|
| 直近レンジ末尾の鮮度 | flush 間隔ぶん遅延（最大 ~5 分の空白） | **ほぼ即時**（未 flush 分を JetStream 補完） |
| クエリレイテンシ | レイクスキャンのみ | +JetStream フェッチ（並行、p95 +<500ms 目標） |
| 障害時 | 影響なし | フェッチ失敗はレイクのみへデグレード |
| 適する場面 | 履歴分析中心・末尾欠落が問題にならない | 運用ダッシュボードで末尾まで見せたい |

> 最新値（`latest=true`）は常に Hot KV が一次のため tail-merge の対象外。cold クエリ（end が現在から離れる）も対象外。

---

## 4. 鮮度を調整する環境変数

| 変数 | 既定 | 効果 |
|---|---|---|
| `PARQUET_FLUSH_INTERVAL`（分） | 5 | 小さく→履歴の鮮度↑/小ファイル増。大きく→鮮度↓/書き込み効率↑ |
| `PARQUET_FLUSH_MAX_ROWS` | 50,000 | 行数到達でも flush（高スループット時の鮮度を底上げ） |
| `PARQUET_TAIL_MERGE_ENABLED` | true | 直近レンジ末尾の即時補完の ON/OFF |
| `PARQUET_TAIL_LOOKBACK_SEC` | 900 | この秒数以内の `end` を tail-merge 対象に |
| `LAKE_COMPACTION_INTERVAL`（分） | 15 | rollup/compact 生成の周期（集計クエリの鮮度に影響） |

詳細は [ルート README の環境変数表](../README.md)（ConnectorWorker 節）。

---

## 5. 監視すべき鮮度 KPI

| KPI | 意味 | 健全な状態 |
|---|---|---|
| `parquet_writer.freshness_lag`（histogram） | flush 時の `now − max(event time)` | p95 ≤ flush 間隔 + 60s |
| ParquetLakeWriter の consumer pending（gauge） | writer がレイクへ追従できているか | 持続負荷で**単調増加しない** |
| `parquet_lake.tail_merge_rows` / `tail_merge_errors`（counter） | tail-merge の補完行数 / デグレード回数 | errors が継続的に増えない |

上記は [oss-warm-parquet-lake.md](oss-warm-parquet-lake.md) / [observability-baseline.md](observability-baseline.md) に定義。
本番スケールでの実測は [#297](https://github.com/takashikasuya/gutp-building-os-oss/issues/297)。

---

## 6. Point 別「鮮度切れ」判定 — 期待周期ベース（#183）

上記 §1–§5 は「イベント→読める」までの**配信**鮮度。本節は運用ダッシュボード（オペレータ ホーム
`/home`、ポイント詳細）が「そのポイントは**そもそも届いているか**」を判定する **stale 判定**の閾値モデルです。

設備データの期待周期は Point ごとに大きく異なります（室温 1 分 / 電力量 30 分 / 設備状態 5 秒 /
保守点検値 1 日）。固定 300 秒の一律閾値だと、速いポイントは誤検知し、遅いポイントは検出が遅れます。そこで
判定閾値を**期待周期から導出**します:

```
判定閾値 = expectedInterval × N      （N = telemetry.staleIntervalMultiplier, 既定 3。
                                       管理者が変更すると runtime で全ロールに反映される）
```

期待周期は以下の階層で解決し、**最初に見つかった値**を使います（most specific first）:

```
point-specific expected interval   （Twin / Point metadata の sbco:interval）
  → device default
  → gateway default
  → (無し) ⇒ system default 閾値 telemetry.staleThresholdSeconds（現行の 300s）へフォールバック
             ※期待周期が無い場合は倍率を掛けず、従来どおり 300s をそのまま用いる
```

現状 Twin が持つのは **point 単位**（`sbco:interval`, 秒）のみ。device / gateway 既定は Twin に未モデル化
のため resolver 上は受け口だけ用意し（API 非互換を出さずに後日配線可能）、本スライスでは point + system の
2 段で稼働します。

### 実装

| レイヤ | 実体 |
|---|---|
| 期待周期→閾値（純粋関数） | `web-client/src/lib/telemetry/freshness-threshold.ts`（`resolveExpectedIntervalSeconds` / `resolveStaleThresholdSeconds`, `DEFAULT_STALE_INTERVAL_MULTIPLIER`） |
| Point 別閾値の適用 | `classifyPointFreshness`（`PointLastSeen.thresholdSeconds` で per-point 上書き）／ `loadPointsFreshness`（期待周期マップ + 倍率から各点の閾値を算出） |
| 期待周期の供給 | `Point.interval`（aspida `interval`／`sbco:interval`）。Twin seed は既に `sbco:interval` を書き込むが、読み取り経路（OxiGraph mapper / SPARQL projection）が未配線だったのを本スライスで有効化 |
| 既定値・設定 | `SettingsRegistry`（#148, 管理者が編集）: `telemetry.staleThresholdSeconds`（300, 周期未設定時の既定閾値）と `telemetry.staleIntervalMultiplier`（3, 倍率 N）。両方の**実効値**（既定 + 管理者 override）は全ロール可の `GET /api/telemetry/config`（`TelemetryConfigController`, `TelemetryThresholds`）で公開 |
| 閾値の供給 | フロントは façcade の `getTelemetryConfig()`（`lib/telemetry/repository.ts`, セッションキャッシュ + 失敗時は定数へフォールバック）で上記を取得し、home loaders / ポイント詳細（`TelemetryHotData`）へ配線。管理者が倍率を変更すると同一 interval/age の判定が stale⇄fresh に切り替わる（回帰: `telemetry-config.test.ts`） |

> **all-role read サーフェス（#183, #210 レビュー対応で実装）**: 鮮度判定は home / ポイント詳細など**全ロール**の
> 画面で走るため、閾値を editable 設定にするには非管理者でも読めるサーフェスが要る（`GET /api/system/settings` は
> admin 限定）。そこで実効閾値の 2 値だけを返す `GET /api/telemetry/config` を追加し（他の設定は漏らさない）、
> フロントの `getTelemetryConfig()` 経由で home / ポイント詳細の両方へ同じ値を配線した。これで管理者の倍率変更が
> runtime で反映される（旧 false affordance の解消）。
>
> **残りのフォローアップ**: device / gateway 既定周期は Twin 未モデル化のため resolver は受け口のみ（point +
> system の 2 段で稼働）。`GET /api/telemetry/config` の aspida 型生成（現状は bespoke fetch）も後続。

---

## 7. 関連

- [oss-tier-architecture.md](oss-tier-architecture.md) — Hot/Warm/Cold 階層と Query Router
- [oss-warm-parquet-lake.md](oss-warm-parquet-lake.md) — 既定の Parquet レイク（背景/構成/KPI）
- [oss-parquet-tail-merge.md](oss-parquet-tail-merge.md) — tail-merge 設計
- [evaluation-summary.md](evaluation-summary.md) — E2E 実測と妥当性
- [oss-production-deployment.md](oss-production-deployment.md) — 本番デプロイ構成
