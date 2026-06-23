# PRD: ウォーム層の構成選択 — Parquet 直接書き込みレイクへの統合

Epic: [#211](https://github.com/takashikasuya/gutp-building-os-oss/issues/211)
ADR: [0002 — Warm 層既定を Parquet レイクとし TimescaleDB を opt-in にする](adr/0002-warm-store-parquet-lake.md)
関連: [oss-tier-architecture.md](oss-tier-architecture.md)（現行 Hot/Warm/Cold の正）

---

## 1. 背景と目的（コスト構造）

現行のテレメトリ階層は **Hot（NATS KV 最新値）→ Warm（TimescaleDB）→ Cold（MinIO Parquet）** で、
Warm は DB インスタンスの**常時稼働費**（コンピュート + SSD ストレージ）を要する。さらに
ColdExportWorker が Warm から Cold へ二重に書き出すため、同じデータが DB と Object Storage の
両方に存在する期間が生じる。

| 層 | 現行 | コスト特性 |
|---|---|---|
| Hot | NATS KV `telemetry-latest` | 最新値のみ・無視できる規模 |
| Warm | TimescaleDB hypertable（retention 120d） | **DB 常時稼働 + SSD**（GB あたり Object Storage の 4〜10 倍） |
| Cold | MinIO Parquet+Zstd（ColdExportWorker 経由） | Object Storage 従量のみ |

本 PRD は Warm を **`building-os.validated.telemetry` からの Parquet 直接書き込み**に置き換え、
Warm/Cold を**単一 Parquet レイク**に統合する構成を追加する。DB 中心構成（timescale モード）も
env 切替で維持し、**構成を選択可能**にする。毎分 1 万件規模で DB 中心構成比の大幅な
ランニングコスト削減（ストレージ ≥80% 削減 + DB 稼働費ゼロ）を狙う。

```
現行（timescale モード）:
building-os.validated.telemetry → NATS KV（最新値）
                                → telemetry-consumer → TimescaleDB（Warm）
                                                        → ColdExportWorker → Parquet → MinIO（Cold）

新規定（parquet モード）:
building-os.validated.telemetry → NATS KV（最新値）
                                → ParquetWriterWorker → Parquet+Zstd → MinIO（単一レイク = Warm 兼 Cold）
```

## 2. ゴール / 非ゴール

### ゴール

- env `WARM_STORE=parquet|timescale` でウォーム層構成を選択可能にする（**既定 parquet**）
- parquet モードではテレメトリの read/write が **TimescaleDB なしで成立**する
- API（`/telemetries/*`）のエンドポイント・レスポンス形（`ValidTelemetryData[]`）は両モードで不変（web-client 無修正）
- 切替効果を KPI（§7）で計測・評価可能にする（[#219](https://github.com/takashikasuya/gutp-building-os-oss/issues/219)）

### 非ゴール（後続 issue として起票済み）

- 秒オーダーの末尾鮮度（JetStream tail マージ）→ [#220](https://github.com/takashikasuya/gutp-building-os-oss/issues/220)
- DuckDB 等の外部クエリエンジン → [#221](https://github.com/takashikasuya/gutp-building-os-oss/issues/221)
- 事前集計 parquet（hourly rollup）→ [#222](https://github.com/takashikasuya/gutp-building-os-oss/issues/222)
- point-control 結果リポジトリ（`IPointControlRepository`）の DB 依存解消（§8 参照）

## 3. アーキテクチャ

### 3.1 統合レイクのレイアウト

既存 `cold` バケットと既存パーティションスキームを「レイク」に昇格する（既存 export 済みデータが
そのまま資産になり、移行コストが最小）。

```
bucket: cold
key:    building_id={building}/year={YYYY}/month={MM}/day={DD}/hour={HH}/
            part-{firstSeq}-{lastSeq}.parquet     # 新 ParquetWriterWorker（JetStream seq で決定的命名）
            part-{yyyyMMddHHmmss}.parquet          # 既存 ColdExportWorker（timescale モード）
            compact-{yyyyMMddHH}.parquet           # CompactionWorker 出力（#217）
```

- **パーティションはイベント時刻基準**（行の `datetime` から building × hour でグループ化）。
  既存 ColdExportWorker はウィンドウ開始時刻でキーを作るため時間境界で行がずれる既知の歪みが
  あるが、reader 側が **±1 時間のグレース幅**で列挙して両方式を吸収する。
- **Parquet スキーマは既存 8 カラムを変更しない**: `point_id` / `building` / `device_id` / `name`
  (string), `value` (double?), `time` (timestamp), `data` (string=JSON), `id` (string)。Zstd 圧縮。
- **決定的命名**: 新 writer は JetStream stream sequence で `part-{firstSeq}-{lastSeq}.parquet` と
  命名する。ack 前クラッシュ→再配信時に**同一オブジェクトへの上書き = 冪等**。
- **小ファイル対策**: 確定した過去 hour パーティションを CompactionWorker（#217）が
  `compact-{yyyyMMddHH}.parquet` に統合（id dedup 込み）。目標 1 building-hour ≤ 2 オブジェクト。
- **retention**: MinIO ILM（env `LAKE_RETENTION_DAYS`、未設定=無期限）。TimescaleDB retention
  policy（120d）の代替。

### 3.2 書き込み — ParquetWriterWorker（[#213](https://github.com/takashikasuya/gutp-building-os-oss/issues/213)）

- **.NET 実装・ConnectorWorker 内 BackgroundService**。**定常運用では `WARM_STORE=parquet` の
  ConnectorWorker でのみ登録**（同モードでは ColdExportWorker と排他）。Python telemetry-consumer
  の置換（Python 版は timescale モード専用に残置 — メトリクスなし・テストなしの弱点はこの置換で
  解消する）。移行併走（§6）は **timescale モードの本番ワーカーとは別に、`WARM_STORE=parquet` を
  設定した追加の ConnectorWorker インスタンス**を起動してレイクへ書く（durable consumer が独立
  しているため安全）。§4.2 のモード別表は定常運用の前提。
- durable pull consumer（既存 `telemetry-consumer-worker` と独立 → 併走・段階移行可能）。
  DeliverPolicy=All（stream 残存分も取り込み、重複は冪等で吸収）。
- **at-least-once**: バッチ蓄積 → Parquet PUT 成功後にまとめて ack。AckWait > flush 間隔。
  flush は `PARQUET_FLUSH_INTERVAL`（分、5–15、既定 5）または `PARQUET_FLUSH_MAX_ROWS`
  （既定 50,000）到達時。
- エンベロープ `ValidMessageJson.telemetries[]`（1 メッセージ複数件）の展開・building×hour
  グループ化・`id` dedup は pure クラス `TelemetryBatchAccumulator` に分離（unit test 対象）。
- **前提: stream limits の明示設定**。`BUILDING_OS_VALIDATED` は現状 LimitsPolicy 既定
  （+ `nats-server.conf` の max_file_store=1GB）のため、AckWait + flush 間隔の間にストリームが
  溢れると Discard(Old) で未 ack 分が消える。MaxAge/MaxBytes を明示し、flush 間隔中の最大流量
  に耐えるサイズ設計とする（`NatsStreamTopology` 変更 = 全ワーカー影響）。
- メトリクス（OTLP）: rows / flushes / flush_duration(hist) / failures /
  freshness_lag(hist: flush 時 now − max(event time)) / consumer pending(gauge)。

### 3.3 読み出し — ParquetLakeTelemetryStore（[#214](https://github.com/takashikasuya/gutp-building-os-oss/issues/214)）

単一クラスが **`IWarmTelemetryStore` と `IColdTelemetryStore` の両方を実装**し、warm/cold に同一
インスタンスを注入する。これにより `OssTelemetryQueryRouter` は**無変更**（warm/cold 境界が
どちらに転んでも同じレイクを読む）。

- `QueryAsync`: building prefix 一覧（IMemoryCache 5 分）→ `PartitionKeyRangePlanner`（pure:
  期間→prefix 集合、±1h グレース、日/月単位への縮約）→ streaming read → point_id/時間フィルタ
  → `id` dedup → 時刻ソート。`compact-*` が存在する hour は `part-*` をスキップ。
- `QueryLatestAsync`: Hot KV が一次（Router 既存動作）。fallback は
  `PARQUET_LATEST_LOOKBACK_HOURS`（既定 24）を新しい hour から遡る。
- multi-point: `QueryMultiAsync` で 1 スキャン複数 point（N 倍読み回避、#215）。
- Hour/Day 集計: v1 は **aggregate-on-read**（pure `TelemetryAggregator` + Router 既存 5 分
  キャッシュ）。値の意味は TimescaleDB `time_bucket` の avg/min/max と同義。
- 大範囲ガード: `PARQUET_QUERY_MAX_FILES`。

### 3.4 鮮度のセマンティクス（SLA）

| 問合せ | parquet モードの鮮度 |
|---|---|
| 最新値（latest=true） | 即時（NATS KV、経路不変） |
| 範囲クエリの末尾 | **最大 flush 間隔（5–15分）+ 処理遅延** の遅れ |

取り込み全体の配信保証は「validated に載った分の at-least-once」が上限（raw→validated は
ADR 0001 の at-most-once）。validated→レイクは at-least-once + 三重 dedup（決定的命名 /
読み出し id dedup / compaction dedup）で**損失ゼロ・重複非表示**。

## 4. 切替仕様

### 4.1 env

| 変数 | 既定 | 説明 |
|---|---|---|
| `WARM_STORE` | `parquet` | `parquet`=レイク統合 / `timescale`=現行 DB 構成 |
| `PARQUET_FLUSH_INTERVAL` | `5` | flush 間隔（分、5–15） |
| `PARQUET_FLUSH_MAX_ROWS` | `50000` | 行数 flush 閾値 |
| `PARQUET_LATEST_LOOKBACK_HOURS` | `24` | latest fallback の遡り上限 |
| `PARQUET_QUERY_MAX_FILES` | （実装時決定） | 1 クエリの最大読みファイル数ガード |
| `LAKE_RETENTION_DAYS` | 未設定=無期限 | MinIO ILM による保持期間 |

### 4.2 モード別の有効コンポーネント

| コンポーネント | parquet（既定） | timescale |
|---|---|---|
| NATS KV 最新値（Hot） | ✓ | ✓ |
| ParquetWriterWorker | ✓ | — |
| Python telemetry-consumer | — | ✓（compose `profiles: [timescale]`） |
| TimescaleDB warm/agg ストア | — | ✓ |
| ColdExportWorker | — | ✓ |
| CompactionWorker（#217） | ✓ | — |
| `ParquetLakeTelemetryStore`（warm+cold） | ✓ | —（cold は修正済み reader） |

- compose / Helm / k8s に `WARM_STORE` を配線（[#216](https://github.com/takashikasuya/gutp-building-os-oss/issues/216)）。
- **既定変更は breaking change**: 既存環境が無指定アップグレードで parquet モードになる。
  E の PR で CHANGELOG/README に明記し、`WARM_STORE=timescale` での復帰を周知。

## 5. 既存バグ・ギャップの修正（両モードに効く、[#212](https://github.com/takashikasuya/gutp-building-os-oss/issues/212)）

1. **cold reader のレイアウト不一致**: 書き込みは `building_id={b}/year=.../hour=.../part-*.parquet`
   だが、読み（`MinioParquetColdTelemetryStore`）は `{year}/{month}/` prefix で列挙しており
   ヒットしない。writer レイアウトに合わせ、時間プルーニングを実装する。
2. **cold store の DI 未配線**: ApiServer の DI で cold store が router/`OssTelemetryDatabase` に
   注入されておらず、cold クエリは warm フォールバックで動いている。MINIO 設定時に配線する。

## 6. 移行とロールバック（[#218](https://github.com/takashikasuya/gutp-building-os-oss/issues/218)）

新規デプロイは最初から parquet モード（移行不要）。既存環境は:

1. **併走**: 本番の timescale モードワーカーはそのままに、**`WARM_STORE=parquet` を設定した
   別の ConnectorWorker インスタンス**（移行用、compose の専用プロファイル等で明示起動）を
   並行稼働させ、レイクへの書き込みを開始する。ParquetWriterWorker は独立した durable consumer
   を使うため、既存の telemetry-consumer と競合しない。§4.2 の表は定常運用の前提であり、
   この併走は移行期間限定の例外。
2. **backfill**: CLI で TimescaleDB の既存データをレイクへ一括移行（行数照合・再実行安全）
3. **切替**: `WARM_STORE=parquet` に変更 → telemetry-consumer / ColdExportWorker 停止
4. **DB 停止**: 検証後に TimescaleDB を停止（テレメトリ用途分）
5. **ロールバック**: `WARM_STORE=timescale` に戻すだけ（データは消さない。併走期間中の
   timescale 側欠落分は backfill の逆向きはせず、レイク読みで補完される）

## 7. KPI と評価方法（[#219](https://github.com/takashikasuya/gutp-building-os-oss/issues/219) で計測可能にする）

| 分類 | KPI | 合否基準 | 計測方法 |
|---|---|---|---|
| コスト | telemetry 経路の DB 依存 | parquet モードで TimescaleDB **停止状態でも** telemetry read/write 成立 | #216 統合テスト + compose で DB 停止 smoke |
| コスト | ストレージ効率 | parquet bytes/row が timescale 非圧縮比 **≥80% 削減** | `measure_compression.sh` 拡張 + バケットサイズ |
| コスト | ファイル数 | compaction 後 1 building-hour **≤ 2** オブジェクト | MinIO list + メトリクス |
| 性能 | latest p95 | **< 500ms**（Hot KV 経路不変、劣化なし） | k6 s5_api_read |
| 性能 | warm 24h/1point p95 | **< 2s** | k6 新シナリオ |
| 性能 | cold 7d/1point p95 | **< 5s**（既存目標維持） | k6 |
| 性能 | hour/day 集計 p95 | キャッシュ cold **< 3s** / hit **< 100ms** | k6 |
| 鮮度 | ingest→queryable lag p95 | **≤ flush 間隔 + 60s** | `parquet_writer.freshness_lag` histogram |
| スループット | writer 追従性 | 持続負荷で consumer pending が**単調増加しない** | pending gauge + 負荷ハーネス |
| 信頼性 | flush 失敗率 | **< 0.1%**、失敗時データ損失ゼロ（再配信で回復） | failures カウンタ + 障害注入 |

評価は両モードで同一負荷の k6 比較を行い、`Tools/e2e-performance/results/` に
レポート（report.md 慣習）として残す。未達 KPI は改善 issue（#220/#221/#222 等）に紐づける。
計測手順・スクリプト・レポート様式は [oss-warm-parquet-kpi.md](oss-warm-parquet-kpi.md)（#219）
を参照（`k6/s9_warm_kpi.js` / `measure_lake_storage.sh` / レポートテンプレート）。

## 8. リスクと緩和策

| リスク | 緩和策 |
|---|---|
| MinIO 可用性が read のクリティカルパス化 | latest は Hot KV で独立。Router の tier 別 degrade（空配列 + メトリクス）は既存踏襲。MinIO を system-status の health target に追加 |
| 大範囲スキャンのメモリ | ファイル単位 streaming + row group 単位処理 + `PARQUET_QUERY_MAX_FILES` ガード |
| 重複行 | 三重防御: 決定的命名（再配信）/ 読み出し id dedup / compaction dedup。publish 側 `Nats-Msg-Id` 付与（コネクタ再起動の重複 publish 対策）は後続改善 |
| stream 溢れによる取りこぼし | `BUILDING_OS_VALIDATED` の MaxAge/MaxBytes 明示 + AckWait/flush 整合（#213 必須要件） |
| continuous aggregate 喪失 | aggregate-on-read は time_bucket と同義。性能は Router キャッシュで吸収、必要なら #222 |
| 既定 parquet 化の挙動変更 | breaking change 明記（#216）、ロールバックは env 1 つ |
| **point-control の DB 依存が残る** | `IPointControlRepository` は `TIMESCALE_CONNECTION_STRING` 必須のため「DB 停止可」はテレメトリ経路限定。コスト訴求は hypertable/telemetry 分と明記（解消は本 Epic 非ゴール） |
| Python telemetry-consumer | timescale モード専用に残置（profile 化、削除しない） |
| `cold_export_log` | parquet モードでは不要（JetStream ack floor + 決定的命名が台帳）。timescale モードは現行維持 |
| DLQ 未実装（poison message） | 既知の将来対応（ADR 0001 と同じ位置づけ）。writer はパース不能行をログ + メトリクスでスキップ |
| NATS KV に TTL なし | 最新値のみ（history=1）で point 数に比例の固定規模。監視のみ |

## 9. バックログ

| issue | 内容 | 依存 |
|---|---|---|
| [#211](https://github.com/takashikasuya/gutp-building-os-oss/issues/211) | Epic | — |
| [#212](https://github.com/takashikasuya/gutp-building-os-oss/issues/212) | A: cold reader レイアウト不一致修正 + Planner + DI 配線 | なし |
| [#213](https://github.com/takashikasuya/gutp-building-os-oss/issues/213) | B: ParquetWriterWorker + stream limits | なし |
| [#214](https://github.com/takashikasuya/gutp-building-os-oss/issues/214) | C: ParquetLakeTelemetryStore（warm/cold 統合読み） | #212 |
| [#215](https://github.com/takashikasuya/gutp-building-os-oss/issues/215) | D: aggregate-on-read + multi-point | #214 |
| [#216](https://github.com/takashikasuya/gutp-building-os-oss/issues/216) | E: WARM_STORE 切替・既定 parquet 化（リリースゲート） | #213 #214 #215 |
| [#217](https://github.com/takashikasuya/gutp-building-os-oss/issues/217) | F: CompactionWorker + retention | #213 #214 |
| [#218](https://github.com/takashikasuya/gutp-building-os-oss/issues/218) | G: backfill + 移行 runbook | #212 #216 |
| [#219](https://github.com/takashikasuya/gutp-building-os-oss/issues/219) | H: KPI 計測・比較レポート（評価ゲート） | #216（#217 推奨） |
| [#220](https://github.com/takashikasuya/gutp-building-os-oss/issues/220) | 後続: JetStream tail マージ | KPI 実測後判断 |
| [#221](https://github.com/takashikasuya/gutp-building-os-oss/issues/221) | 後続: DuckDB spike | KPI 実測後判断 |
| [#222](https://github.com/takashikasuya/gutp-building-os-oss/issues/222) | 後続: 事前集計 parquet | KPI 実測後判断 |

実装順: #212 →（#213 並行）→ #214 → #215 → #216 → #217 / #218 / #219 並行。
