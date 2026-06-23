# Warm 層の既定を Parquet レイクとし、TimescaleDB を opt-in にする

テレメトリのウォーム層を `building-os.validated.telemetry` からの Parquet 直接書き込み
（MinIO/Object Storage、Cold と単一レイクに統合）とし、env `WARM_STORE` の**既定を `parquet`**
にする。TimescaleDB 構成は `WARM_STORE=timescale` の opt-in として完全互換で残す。
最新値は引き続き NATS KV（Hot）が担い、範囲クエリの末尾はフラッシュ間隔（5–15 分）の遅延を
許容する（Epic #211 / PRD `docs/oss-warm-parquet-lake.md`）。

ランニングコスト最小化が動機: DB インスタンスの常時稼働費（コンピュート + SSD）を排し、
Object Storage の従量課金のみでテレメトリ履歴を保持する。

## Considered Options

**TimescaleDB 継続（現行）**: 検索性能・continuous aggregate・運用実績は最良。ただし常時稼働の
DB コストが支配的で、Cold（Parquet）との二重保存も発生する。コスト最小という今回の目的に
合わないため既定からは外すが、性能要件が厳しい環境向けに opt-in として残す。

**Parquet on Object Storage（採用）**: ストレージ単価が最安（SSD 比 1/4〜1/10）、DB 稼働費ゼロ、
Cold と同一フォーマットで層の統合が可能。既存の Cold エクスポート実装（Parquet.Net + Zstd +
パーティションスキーム）と読み出し抽象（`IWarmTelemetryStore` 等）が流用でき、追加依存もない。
弱点（末尾鮮度・集計性能・小ファイル）は Hot KV / aggregate-on-read + キャッシュ /
コンパクションで補い、KPI（#219）で実測検証する。

**JSONL on Object Storage**: 中間バッファとしては簡便だが、最終保存形式としては検索時の
パースコストとスキャン効率が悪い。採用しない（writer 内部のメモリバッファで十分）。

**ClickHouse**: 集計・分析性能は最有力だが DB サーバーの常時稼働が必要で、コスト最小では
Parquet 直保存に劣る。「検索性能を買う」選択肢として、必要になれば将来の構成追加候補。

**MongoDB**: 大量時系列の長期保持はストレージ・インデックス費が嵩み、集計分析でも
Parquet/ClickHouse に劣る。テレメトリ本体には不向きのため採用しない。

## Consequences

- `WARM_STORE` 既定変更は **breaking change**: 既存環境が無指定アップグレードで parquet モードに
  なる。CHANGELOG/README に明記し、`WARM_STORE=timescale` での復帰（ロールバック = env 1 つ）を
  周知する（#216）。
- parquet モードのウォーム読みは末尾がフラッシュ間隔ぶん遅延する。最新値は Hot KV が担保。
  鮮度ゼロ化が必要なら JetStream tail マージ（#220）を追加する。
- validated→レイクは at-least-once + 三重 dedup（JetStream seq の決定的ファイル命名 /
  読み出し id dedup / コンパクション dedup）。前提として `BUILDING_OS_VALIDATED` の
  MaxAge/MaxBytes を明示設定し、flush + AckWait の間に Discard で未 ack 分が消えないようにする
  （#213）。なお raw→validated は ADR 0001 の at-most-once のままであり、取り込み全体の保証は
  「validated に載った分」が上限。
- TimescaleDB の continuous aggregate は parquet モードでは使えない。Hour/Day は
  aggregate-on-read（time_bucket と同義の畳み込み）+ Router キャッシュで代替し、不足すれば
  事前集計 parquet（#222）を導入する。
- MinIO がウォーム読みのクリティカルパスに入る。Router の tier 別 degrade（既存）を踏襲し、
  MinIO を system-status の health target に追加する。
- `IPointControlRepository` は `TIMESCALE_CONNECTION_STRING` を要求し続けるため、「DB 停止可」は
  テレメトリ経路に限定される（解消は本 Epic の非ゴール）。
- Python telemetry-consumer と ColdExportWorker は timescale モード専用となる（削除しない）。
- **TimescaleDB のライセンス留意（opt-in 利用時）**: TimescaleDB は **`tsl/` ディレクトリ外が Apache 2.0、
  `tsl/` 配下が Timescale License（TSL）** の二層構成。圧縮 / columnstore 等の一部機能は TSL に含まれる。
  既定の parquet モードでは TimescaleDB に依存しないが、`WARM_STORE=timescale` を選ぶ場合は **使用するコンテナ
  イメージ・機能・拡張が Apache-only で成立するか**（TSL 機能に依存しないか）を採用前に確認すること。
  外部配布形態（hosted/embedded）では特に影響する。
