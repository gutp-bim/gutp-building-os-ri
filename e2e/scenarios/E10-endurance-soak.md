# E10 — 長時間ソーク（endurance soak）

## 背景・位置づけ
[#297](https://github.com/gutp-bim/gutp-building-os-ri/issues/297) で THX 点リスト（1,865 点/300 秒周期、
MQTT 経路）による 24 時間 E2E 試験を実施した。データ経路は 537,121/537,121 件を完全一致で受理・永続化し、
再起動・OOM はゼロだったが、Connector Worker RSS が 281.6→497.5 MiB、Building OS NATS RSS が 44.96→145.0 MiB
増加した。毎時 Parquet compaction に対応する鋸歯状変動は説明がつくが、それを差し引いた定常 baseline が
上昇していないかは複数日試験でないと判断できない（#297 の acceptance criteria は **最低 72 時間**）。

本軸 E10 は、その 72h 版に至る前の **e2e/ 評価軸への統合 + 反復可能な短時間版**（既定 4–6h、この
worktree での初回実行時点）。`run-all.sh` の既定 `ONLY` には含めない — 個別実行のみ。

## #297 以降の改修との関係（2026-08-24 時点の見直し）
- テレメトリ API の `value` が union 型化され `valueText`/`valueBool` が撤廃された（#358/#359/#364/#366/#369）。
  **Parquet の保存列（4 列: value_num/value_text/value_bool/state 相当）は変更されていない**（#359 の PR 説明
  および #348 の計測記録に明記）— ソーク試験が直接依存する `quality_checker.py` の Parquet 列読み取りは
  影響を受けない。API 応答のみを見る場合は union 型のデコードが必要だが、本軸は Parquet 直読 + NATS
  monitoring + gRPC ingress のみを使い、API JSON は経路に含まれないため実質無関係。
- 性能試験のシーダー（#338/#300）と品質チェッカー（#342/#343/#345）が現行 twin バリデーション・判別値に
  追随済み — 本ソークが再利用する `s10_pointlist_integrity.insert_point` / `quality_checker.py` は現行に整合。
- twin 側の変更（levels 直下の equipment、`rec:Room`→`sbco:Room` materialize）はソークの対象外（point
  resolution の可否は起動直後の `wait_visible` で確認するのみ）。
- ADR-0005 の閾値設定手段が明確化された（#367）。E10 のメモリ閾値はまだこの ADR の管理下にない
  informational KPI のため対象外だが、baseline が確定した将来の #297 完了時に検討する。

## 測定経路
[plan.md §0](../plan.md) の正本経路と同じ **gRPC GatewayIngress**（#297 は MQTT 経路だった点が異なる —
plan.md が ingest 評価の第一経路と定めているのが gRPC のため、e2e/ 統合版はこちらを使う。MQTT 経路の長時間
特性は別途 nexus-gateway 側で評価する）。

## 試験条件（既定）
| 項目 | 値 |
|---|---|
| 点数 | 1,865（#297 の THX スケールを踏襲） |
| 周期換算レート | 約 6/s（1,865 点 ÷ 300 秒 ≈ 6.2/s） |
| 継続時間 | 4–6h（この worktree での実施値。#297 の確定版は ≥72h） |
| flush/compaction 間隔 | アプリ既定値のまま（意図的に速めない — 実運用の鋸歯パターンを観測するため） |
| リソースサンプリング間隔 | 60s |
| gRPC ストリーム再接続間隔 | 300s 毎（#297 は単一 24h ストリーム。本版は connector-worker が
  試験中に再起動しても継続測定できるよう小分けにする）。`CHUNK_SECONDS=0` で単一ストリームに切替可 |
| .NET runtime メトリクス | 既定 OFF。`PROMETHEUS_URL` 指定時のみ（[#370 の調査](#370-rss-の内訳を分離する)） |
| seed 可視化待ちの上限 | 600s（`--seed-visible-timeout`）。下記の通り点数を増やすならここも上げる |

### seed 直後の cold load が支配的なコストである（点数を変えるとき必読）

`IPointMetadataCache` の cold load は OxiGraph へのポイント一括 SPARQL 1 本で、これは**点数に対して
二次で効く**。同一ホスト・アイドル状態での実測:

| 点数 | 一括ロード SPARQL |
|---|---|
| 100 | 0.25s |
| 500 | 2.15s |
| 1,000 | 7.62s |
| 1,865（既定） | **23.3s** |
| 3,000 | **56.6s** |

seed 直後で OxiGraph がまだ落ち着いていない状態や CPU 競合下ではさらに伸び、.NET の既定
`HttpClient.Timeout`（100s）に届くこともある。この軸が seed 後すぐ 1 フレーム流して可視化を確認する
のはそのロードを 1 回踏むということなので、小規模軸（E5 など）向けの `s10.wait_visible`（45s 予算・
1 試行 60s の gRPC deadline を裸で投げる）では既定の 1,865 点にすら足りず、超過時は
DEADLINE_EXCEEDED が例外として伝播して数時間の soak が seed 直後に落ちる。E10 は
`wait_visible_at_scale`（予算 `--seed-visible-timeout`、RPC 例外は「まだ見えない」として再試行）を
使う。`--points` を既定より大きくするなら、上表の二次カーブに沿って予算も上げること。

> この二次コストは harness 側の都合ではなく、実運用の cold start / TTL リフレッシュでも同じだけ
> かかる。[#371](https://github.com/gutp-bim/gutp-building-os-ri/issues/371) の直接原因はここにある。

## 手順
1. `docker compose -f docker-compose.oss.yaml up -d`（GRPC_INGRESS_PORT は compose 既定で 5051 有効）。
2. `DURATION_HOURS=4 RATE=6.2167 POINTS=1865 bash Tools/e2e-performance/s19_endurance_soak.sh <out-dir>`
   （内部で `s19_endurance_soak.py` が下記を並行実行）:
   - gRPC ストリームで持続負荷を送信し続ける（chunk 毎に sent/accepted を記録）。
   - 60 秒毎に `docker stats` で対象コンテナ（connector-worker/nats/oxigraph/minio。API Server は
     gRPC ingress 経路に含まれないため本軸では起動・計測しない）の RSS、
     `docker inspect` で再起動回数・OOM有無、各サービスの health エンドポイント、NATS の
     `BUILDING_OS_VALIDATED` consumer pending（`kpi_sampler.py` のロジックを再利用）をサンプリングする。
3. 終了後、`quality_checker.py`（parquet mode）で送信数と lake 永続化数を突き合わせ、loss/duplicate を
   算出する。
4. `resource-timeseries.jsonl` / `ingest-timeseries.jsonl` の生データと `E10-soak.json`（gate 用の
   `{axis, metrics}`）を `<out-dir>` に残す。`PROMETHEUS_URL` 指定時は
   `runtime-metric-names.json`（`{names, probe}` = 解決した系列名と t=0 の実データ有無。
   `E10-soak.json` の `config.runtime_metric_names` / `config.runtime_metric_probe` にも同じ内容が
   入る）も残る。`ingest-timeseries.jsonl` の各行は `kind` を持ち、`"chunk"` がストリーム 1 本の
   確定記録、`"progress"` が 5 分毎の途中経過（`CHUNK_SECONDS=0` の単一ストリーム run でも
   中断時に送信量が残るようにするため）。

## 重要指標
- **定常性（#297 由来）**: 開始1時間平均 vs 終了1時間平均の RSS、後半のみの回帰スロープ（MiB/h）。
  安全域はまだ未確定のため informational（`report`）。
- **RSS の内訳（#370, `--prometheus` 指定時のみ）**: GC heap（世代別 + LOH/POH）・GC committed・
  累積 allocation・GC 回数・thread pool スレッド数・working set を同じ methodology で出し、
  `connector_worker_rss_minus_gc_committed_*`（RSS − GC committed）で managed heap の増加と
  native / page cache の増加を切り分ける。詳細は [#370 の節](#370-rss-の内訳を分離する)。
- **不変条件（gate 対象）**: コンテナ再起動 0、OOM 0、送受信データ整合（loss ≤1% / dup ≤0.5%）、
  health probe 成功率 ≥99.9%、NATS consumer pending の後半スロープが発散しない（`pending_stable`）。

## #370: RSS の内訳を分離する

### なぜ RSS だけでは決まらないのか
gRPC ingress 経路の 5h ソーク（run `soak-20260823223433`、1,865 点 / 約 6.22 pt/s）で Connector
Worker RSS が 132.0 → 544.4 MiB（+412 MiB）増えた。**5h での増加量が #297 の 24h MQTT 経路ソーク
（281.6 → 497.5 MiB）を上回っている**。一方で後半のみの回帰スロープは 46.78 MiB/h で 5h 平均の
約 82 MiB/h より小さく、収束しつつある可能性もある — 5h では決まらない。

候補は 3 つあるが、`docker stats` の RSS（cgroup の `memory.current` − inactive file pages）は
managed heap / native / GC が commit したまま OS に返していない領域 / page cache が全部混ざった
**一つの数字**なので、この 3 つを分離できない:

| 仮説 | 内容 | 分離に必要な観測 |
|---|---|---|
| (a) | gRPC ingress 経路固有（この run は 300s 毎にストリームを張り直していた） | 再接続の有無を変えた A/B |
| (b) | warm-up（キャッシュ / JIT / GC の commit）で長時間走れば収束する | managed heap と RSS の分離 + より長い run |
| (c) | 本当のリーク | 同上 |

### 計測経路が Prometheus 一択である理由
runtime image は `mcr.microsoft.com/dotnet/aspnet:8.0-noble-chiseled` で shell も coreutils も
dotnet-counters も無いため `docker exec ... cat /proc/1/status` が成立しない（イメージにツールを
足すのは被測定系を変えることになるので不可）。一方
`DotNet/BuildingOS.Shared/Infrastructure/Telemetry/OtelSetup.cs` は既に
`.AddRuntimeInstrumentation()` を呼んでおり、compose も connector-worker に
`OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_SERVICE_NAME` を**常に**設定している。exporter は宛先が
無ければ no-op なので、**observability profile を上げても被測定系のコードも設定も変わらない**
（宛先が生きるかどうかだけが変わる）。

> **摂動の注意（実際の blast radius）**: 「exporter が実際に送る」こと自体が僅かな摂動（15s 毎の
> OTLP エクスポート）である以上に、起動するコンテナは 2 つでは済まない。compose の
> `building-os.otel-collector` は `depends_on: [building-os.tempo, building-os.prometheus,
> building-os.loki]` なので、`.sh` が上げるのは **prometheus / tempo / loki / otel-collector の 4 つ**。
> その結果 collector の **logs パイプラインの宛先が生き**、connector-worker の OTLP ログ出力が
> 実際に送られるようになる（= #370 で見ている当のプロセスのアロケーションが増える）。加えて
> 同一ホストのコンテナが 4 つ増え、CPU / メモリ / page cache を食う。
>
> したがって:
> - **A/B の 2 本は必ず `PROMETHEUS_URL` の有無を揃えて回すこと** — 片側だけ observability を
>   上げた比較は無効。
> - **`PROMETHEUS_URL` 有りの run の RSS 値を、無しの run と直接比べないこと。** #370 の baseline
>   `soak-20260823223433` は observability 無しなので、runtime メトリクス有りの run の
>   `connector_worker_rss_growth_mib_per_hour` をそれと並べるのは別構成同士の比較になる。

### 系列名を決め打ちしない
`OpenTelemetry.Instrumentation.Runtime`（1.17.0）は .NET semantic conventions 採用時に
`process.runtime.dotnet.*` → `dotnet.*` へ改名しており、さらに `prometheusremotewrite` が
`.`→`_` 変換と `_bytes`/`_total` の付与を行う。名前を決め打ちすると「全部 null の列」が静かに
出来上がり、数時間走り終わるまで気付けない。そのため s19 は起動時に
`/api/v1/label/__name__/values` を引いて**実在する系列名から**解決し（世代ラベル名も
`/api/v1/series` のラベルから採り、世代ラベルの**値**も `sum by (…)` で返ってきたものを
そのままキーにする）、解決結果を結果 JSON の `config.runtime_metric_names` にそのまま残す。
解決できなかった概念はサンプリングしない（キー自体を作らない）。

ただし名前が解決できることと、その job のデータが実在することは別物である。
`/api/v1/label/__name__/values` は**保持期間全体**（`prometheus_data` は 15d 保持）の名前を返すので、
昨日の run の系列が残っていれば「collector が Prometheus に届いていない今日の run」でも全概念が
解決してしまう。そこで s19 は解決直後に **`{job=…}` を付けた実データのプローブ**を 1 回行い、

- 結果を `config.runtime_metric_probe`（`{concept: true/false}`）に残す、
- 1 概念も live でなければ起動直後に stderr で警告する、
- live が 1 つも無い間は最初の 15 分だけサンプリング間隔ごとに再解決を試みる
  （Prometheus は永続 TSDB の WAL リプレイ中 `/api/v1/*` に 503 を返すため。`.sh` 側も
  `${PROMETHEUS_URL}/-/ready` を最大 `PROM_READY_TIMEOUT`（既定 180）秒ポーリングしてから
  本体を起動する）。

値の取得は必ず `last_over_time(<metric>{job=…}[2×サンプリング間隔])` 経由で行う。Prometheus の
instant query は既定で 5 分の lookback を持つため、素で引くと remote-write が詰まった系列でも
「最後の値」を返し続け、**凍った GC committed と新鮮な RSS の引き算**が「native が伸びている」
という誤った結論を作る。窓を明示しておけば欠測区間は単に値が返らず、その tick は差分計算に
入らない。

### 実行手順（A/B マトリクス）
```bash
PROM=http://localhost:9090
# (1) 現行条件: 300s 毎にストリーム再接続 + runtime メトリクス
DURATION_HOURS=6 PROMETHEUS_URL=$PROM \
  bash Tools/e2e-performance/s19_endurance_soak.sh results/E10-370-chunk300
# (2) 対照: run 全体を単一ストリーム（#297 と同条件）+ runtime メトリクス
DURATION_HOURS=6 PROMETHEUS_URL=$PROM CHUNK_SECONDS=0 \
  bash Tools/e2e-performance/s19_endurance_soak.sh results/E10-370-single
```
`PROMETHEUS_URL` が指定されると `.sh` が `--profile observability` で
`building-os.prometheus` / `building-os.tempo` / `building-os.loki` / `building-os.otel-collector`
を追加起動する（既定スタックには居ない。tempo/loki が入るのは collector の `depends_on` のため —
上の「摂動の注意」を必ず読むこと）。2 本の `E10-soak.json` の `config` と `metrics` を突き合わせる。

比較する前に `metrics.chunk_count` を見ること。`config.chunk_seconds: 0` は「1 本で流す**指定**」で
あって「1 本で流れた」ではない — 途中で 1 度でも転送に失敗すれば s19 は張り直すので、対照側が
実は 2 本だったということが起こり得る。A/B を「1 本 vs 約 N 本」として読んでよいのは
`chunk_count` がそれを裏付けたときだけ。

### 読み方
`connector_worker_rss_minus_gc_committed_*` が判別指標（**同一 tick の**コンテナ RSS − GC committed
の pairwise 差分。両方揃った tick だけを使う）。

| RSS | rss_minus_gc_committed | 読み |
|---|---|---|
| 増加 | ほぼ横ばい | 増加分は **managed heap**。`gc_heap_gen2/loh/poh` の内訳で「どの世代が滞留しているか」を見る。gen2/LOH が伸びていれば (c) を疑う |
| 増加 | 同程度に増加 | 増加分は **native / page cache / GC が commit したまま返していない領域**。GC 側では説明できない — native alloc（gRPC / Parquet writer のバッファ）か、GC が OS に返していないだけ（= (b) 寄り） |
| 横ばい | — | 収束済み |

**この表を使う前に確認すること**（どちらも満たさないと 2 行目を誤検出する）:

1. **窓が揃っているか。** スロープは常に「その系列自身の後半」で取るので、Prometheus のカバレッジが
   部分的だと RSS スロープと差分スロープが違う時間帯を指す（#370 の run は 5h 平均 82 MiB/h に対し
   後半だけなら 46.78 MiB/h、つまり窓が違えば結論も変わる）。`connector_worker_rss_samples` と
   `connector_worker_rss_minus_gc_committed_samples` を比べ、乖離があるときは
   `connector_worker_rss_growth_mib_per_hour` ではなく **`..._paired`**（差分と同一 tick 集合で
   測った RSS スロープ）と比較する。`*_first_elapsed_h` / `*_last_elapsed_h` に各系列の実測範囲が
   入っている。
2. **subtrahend が更新されているか。** 引いている
   `dotnet.gc.last_collection.memory.committed_size` は名前のとおり **直近の GC 時点**の
   スナップショットで、鮮度は export 間隔ではなく **GC の発生頻度**に律速される。E10 は意図的に
   軽い負荷（1,865 点 / 約 6.22 f/s）なのでコレクション間隔が分単位になり得る。GC がほとんど
   起きていない区間では、RSS だけが動いて差分が伸び「native が増えた」ように見える。
   `connector_worker_gc_collections_growth_per_hour` が十分大きいことを確認すること。

補助指標: `connector_worker_gc_allocated_total_mib_growth_per_hour`（アロケーション圧。
両 run でほぼ同じなら経路の負荷条件は揃っている）、
`connector_worker_thread_pool_thread_count_growth_per_hour`（スレッド増殖もリークの一形態。
ただし取れるのは **thread pool の**スレッド数だけで、専用スレッド — native リソースが張るもの —
はこの系列に含まれない。横ばいでも「スレッドは増えていない」とは言えない）、
`connector_worker_working_set_mib_*`（プロセス視点の RSS。cgroup 値との乖離が page cache の
寄与の目安）。

(a) の判定は A/B の `connector_worker_rss_growth_mib_per_hour` の差で見る — 単一ストリーム側だけ
スロープが明確に小さければ再接続が寄与している。両者が同等なら (a) は棄却され、(b)/(c) の判別は
上表と **より長い run**（#297 の acceptance criteria は ≥72h）に持ち越す。

## 合否（`e2e/kpi-thresholds.yaml`: `E10_endurance_soak`）
再起動 = 0 / OOM = 0 / data_loss_ratio ≤ 1% / duplicate_rate ≤ 0.5% / health_probe_success_rate ≥ 99.9% /
pending_stable = 1。RSS 増加量そのものは report（安全域は #297 の ≥72h 確定試験で定める）。
#370 の runtime 系メトリクス（`connector_worker_gc_*` / `connector_worker_rss_minus_gc_committed_*`）
も **すべて report** — 安全域を決めるための調査そのものなので、ここに閾値を置くと結論を先取りして
しまう。`--prometheus` 無しの run ではこれらの metric は出力されず、gate 表には「—」が並ぶ
（`op: report` は常に INFO なので run を落とすことはない）。

## 既知の限界・#297 との差分
- 4–6h では #297 が懸念した「24h retention 到達後の定常化」やそれ以降の長期トレンドは観測できない
  （NATS `BUILDING_OS_VALIDATED` の MaxAge は 24h — `DotNet/BuildingOS.Shared/Infrastructure/Telemetry/
  ParquetLake/ParquetLakeWriterWorker.cs`）。より長い run で再実行することが前提。
- managed heap / GC heap / LOH / thread 数の分離（#297 の調査項目）は **#370 で対応済み** —
  `PROMETHEUS_URL` 指定時のみ Prometheus 経由でサンプリングする（上記 [#370 の節](#370-rss-の内訳を分離する)）。
  ただし取得は Prometheus のスクレイプ間隔（`oss-stack/prometheus/prometheus.yml`: 30s）と
  connector-worker の `OTEL_METRIC_EXPORT_INTERVAL`（compose 既定 15s）に律速される粒度であり、
  RSS サンプリング（60s 毎）と厳密に同時刻ではない。数時間スケールのトレンド判定には十分だが、
  compaction 直後のような秒スケールの過渡は捉えられない。
- ローカル単一ホスト・単一建物。#263（多棟・大規模 Parquet 保持境界）とは範囲が異なる — 統合は follow-up。
- `run-all.sh` の既定 gate には含めない（数時間かかるため）。CI では実行しない（このリポジトリの CI は
  手動起動のみ）。
