# PRD: gRPC ingest スループット — 1 stream の直列待ち解消（#625）と水平スケール検証（#626）

- 作成日: 2026-11-06
- 対象: `GatewayIngress`（`DotNet/BuildingOS.ConnectorWorker/Connectors/GatewayIngressService.cs` /
  `NatsIngressTelemetryBus.cs`）、`ValidatedTelemetryHotStore`、`Tools/e2e-performance/s22_ingest_rate_sweep.py`
- 根拠となる計測: [`docs/reference/ingest-throughput-sweep.md`](../reference/ingest-throughput-sweep.md)（S22、2026-11-06）
- 位置づけ: **本書は計画のみで実装は含まない。** #625 / #626 はそれぞれ独立に進められるが、#626 は #625 の
  前後で 2 回計測する（§6.3）。
- 守る前提（ADR / 既存決定）: #187「`StreamAck.accepted` = JetStream に永続化済み」、ADR-0001（コネクタは
  at-most-once; gRPC ingress は #187 で例外的に ack 後受理）、`oss-sla-freshness.md`（Hot KV は最新値の一次）、
  #400（`ingest` ロールは水平スケール可、`lake`/`all` は単一レプリカ推奨）。

---

## 1. 背景と問題

S22 で、gRPC `GatewayIngress` の **1 本の client-stream は約 1.5〜1.6k frame/s で頭打ち**になることが分かった。
Worker の CPU は約 1.3 コアで一定、NATS サーバーには 10 倍以上の余力があり、律速は **1 frame ごとに NATS との往復を
2 回直列に待つこと**（レイテンシ律速）である。

```
GatewayIngressService.RunAsync
  while (MoveNext)                          ← frame を 1 件ずつ
    TryIngestAsync
      metadataCache.GetAsync                ← プロセス内 TTL キャッシュ（往復なし）
      bus.PublishAsync
        js.PublishAsync + ack.EnsureSuccess ← 往復 1: JetStream publish-ack（#187）
        ValidatedTelemetryHotStore.WriteAsync
          hot.PutAsync                      ← 往復 2: KV put（ack 待ち）
```

NATS 単体の同形ループ（`nats_bench.py`）は publish-ack のみで 2,866 ops/s、publish-ack + KV put で 1,515 ops/s、
パイプライン publish（window 200）で 24,950 ops/s。Worker の単一 stream 上限（1,583/s）は「publish-ack + KV put の
逐次ループ」でほぼ説明できる。

さらに **1 本の stream は 1 つの Worker に固定される**ため、`WORKER_ROLE=ingest` のレプリカを増やしても、
1 gateway が 1 stream で送る限りその gateway の上限は上がらない。一方「レプリカを増やせば ingest 全体は
水平スケールする」という #400 の主張は、共有 4 コア環境（Worker 2 プロセスで +19%、ホスト CPU 313%）では
検証できなかった（#626）。

## 2. ゴールと非ゴール

### 2.1 ゴール

| # | ゴール | 測り方 |
|---|---|---|
| G1 | 単一 stream の持続レートを **3 倍以上**（約 1.5k → **5,000 frame/s 以上**、達成率 ≥95%）に引き上げる | S22 `--streams 1 --rates 5000`、同一環境で前後比較 |
| G2 | #187 の保証を維持する: `accepted` に数えた frame は JetStream に永続化済み。損失 0・重複 0・`accepted` と Lake 行数の一致 | S22 の判定（損失 ≤1%、重複 ≤0.5% は従来の閾値。目標は 0）+ 既存単体テスト |
| G3 | NATS 障害時の挙動が現状と同等（ack 失敗 → `publish_failed`、stream は継続） | `GatewayIngressServiceTest.StreamTelemetry_PublishFails_*` + 新規統合テスト |
| G4 | `ingest` レプリカ数に対するスループットの線形性と律速箇所を専用ホストで確定し、「水平スケールする」の主張を計測で裏付ける、または制約付きで修正する | §6 |
| G5 | 上限と分割指針（1 stream の上限、高レート gateway の stream 分割）を運用ドキュメントに載せる | §7 |

### 2.2 非ゴール

- 他の validated.telemetry 生産者（HVAC/BACnet 等の `raw.*` コネクタ、MQTT/Hono）の at-least-once 化
  （`telemetry-specification.md` §永続化保証の「既知の残件」のまま）。
- gateway 側（BOWS / nexus-gateway）の実装変更。stream 分割は gateway の責務として文書化するのみ。
- Lake 側の重複排除。~~`ValidTelemetryEntity.id` は `{pointId}.{受信 unix 秒}` で決定的ではなく、本件では触らない。~~
  （2026-11-07 改訂: 実際は受信 unix **ミリ秒**で、Lake は `id` で重複排除するため、同じ point の別の読み取りが
  同一ミリ秒に届くと潰れて失われることが #626 の計測で判明した。gRPC ingress に限り `id` を frame の timestamp
  （unix µs）から作るよう #625 で変更。他の生産者の `id` は対象外のまま。）
- NATS クラスタ化・ストレージ変更の**実施**（#626 で影響を確認するにとどめる）。
- gRPC L4/L7 ロードバランサの導入。

## 3. 設計上の論点と判断

### 3.1 「受理 = 永続化済み」をどう保つか

パイプライン化しても `StreamAck.accepted` を **ack が返った frame だけ**で数えれば #187 は維持できる。
今日と変わるのは「ack を待つ場所」だけで、「ack を待たずに数える」ことはしない。

今日でも stream が途中で切れた場合、永続化済みだが `StreamAck` が届かなかった frame は存在しうる（gateway が再送すれば
重複）。有界ウィンドウ N はこの「不確定な frame」を最大 1 → N 件に広げる。Lake に重複排除は無いので、
**N は gateway の再送ポリシーと合わせて小さく保つ**（既定 128、§4.2）。これは受け入れる制約として文書化する。

### 3.2 Hot KV を frame ごとの待ちから外してよいか

Hot KV は「point ごとの最新 1 件」のキャッシュで、正本は JetStream → Parquet lake である。KV put の失敗は今日も
`TelemetryHotWriteFailures` に計上するだけで publish を失敗させない。したがって **KV put を同期待ちから外しても
耐久性には影響しない**。影響するのは鮮度（`oss-sla-freshness.md` の「ほぼ即時」）で、合体間隔ぶん（既定 100 ms）
遅れる。stale 判定の閾値は秒〜分単位（`expectedInterval × 3`、既定 300 s）なので実用上の影響はなく、
文書の脚注を更新する。

プロセス異常終了時は最大「合体間隔ぶん」の最新値が KV に反映されないが、次 frame で上書きされ、
`latest=true` の読み取りは Parquet へのフォールバック（`PARQUET_LATEST_LOOKBACK_HOURS`）も持つ。

### 3.3 どこで直すか — ingress 専用か、全生産者共通か

`ValidatedTelemetryHotStore.WriteAsync` は `NatsIngressTelemetryBus`（gRPC）と `NatsKvPublisher`（コネクタ）の
両方が通る合流点である。KV 合体は **`IHotTelemetryStore` のデコレータ**として実装し、両経路が同じ振る舞いになる
ようにする（#415 の `ingress.event_lag` もこの合流点で測っているため、計測位置は変えない）。
JetStream のパイプライン化は gRPC stream の生存期間に結び付くため **ingress 側（`GatewayIngressService` +
`IIngressTelemetryBus`）に閉じる**。

### 3.4 複数 frame を 1 メッセージにまとめるか

`ValidMessage.telemetries` は配列なので、同一 stream の連続 frame を 1 publish にまとめる案は消費側の変更なしで
成立する。ただし (a) 1 レコード 16 KiB の上限（`TelemetrySizeLimits.DefaultMaxRecordBytes`）により 1 バッチは
数十 frame が上限、(b) ack の粒度がバッチになり、1 件の失敗で数十 frame が未受理になる、(c) 待ち時間 T ms が加わる。
**A（KV 合体）+ B（パイプライン）だけで G1 に届く見込み**（NATS 単体で pub-only pipelined 24,950/s）のため、
C は **Phase 2 の任意項目**とし、A+B 実装後に効果を測ってから判断する。

### 3.5 順序

同一 stream 内の publish 順序は変えない（1 接続に対して逐次に送る。ack の到着を待たないだけ）。
KV 合体では複数 stream・複数 frame が同じ point に書きうるので、合体テーブルは **`datetime` が新しい方を残す**
（今日の「後勝ち」より厳密）。

## 4. #625 の実装計画（スライス）

各スライスは独立に PR 可能で、環境変数で旧挙動に戻せる。順番は効果の見込みと安全性から **A → B → (C) → D**。

### 4.1 スライス A: Hot KV 書込みの合体（coalesce）

- `IHotTelemetryStore` デコレータ `CoalescingHotTelemetryStore`（`BuildingOS.Shared/Infrastructure/Oss/`）。
  - `PutAsync(pointId, data)` は `ConcurrentDictionary<pointId, ValidTelemetryData>` に **datetime が新しければ**上書き
    して即返る（await しない）。
  - バックグラウンドループが `HOT_STORE_COALESCE_MS`（既定 `100`、`0` = 従来の同期 put）ごと、または保留 point 数が
    `HOT_STORE_COALESCE_MAX_POINTS`（既定 `5000`）に達したときに、スナップショットを取り **有界並列**（既定 32）で
    `inner.PutAsync` する。
  - 失敗は point 単位で `TelemetryHotWriteFailures{source}` に計上（`source` を保留エントリに持たせる）。
  - `IHostedService` として停止時に残りをフラッシュする（タイムアウト付き）。
- `ValidatedTelemetryHotStore.WriteAsync` は変更なし（デコレータ越しに同じ `PutAsync` を呼ぶ）。`RecordEventLag` の
  計測位置は維持（put 前）。合体後の実到達までの遅れは別メトリクス `building_os.hot_store.coalesce_flush_latency`
  （histogram）と `hot_store.coalesced_points`（flush ごとの点数）で可視化する。
- DI: ConnectorWorker と ApiServer の `IHotTelemetryStore` 登録箇所でデコレートする（ApiServer は書き込み経路を
  持たないため ConnectorWorker のみで可。要確認）。
- テスト（`BuildingOS.Shared.Test`）: 後勝ち/新しい datetime 優先、interval・max-points の両トリガ、put 失敗の
  隔離、停止時フラッシュ、`0` で従来どおり同期。
- 期待効果: 単一 stream 約 1.5k → 約 2.8k frame/s（NATS 単体 1,515 → 2,866 と同比）。

### 4.2 スライス B: JetStream publish の有界ウィンドウ化

- `IIngressTelemetryBus` に `ValueTask<Task> PublishPipelinedAsync(subject, message, ct)` を追加（または既存
  `PublishAsync` の戻りを「送信完了」と「ack 完了」に分ける）。`NatsIngressTelemetryBus` は
  `INatsJSContext.PublishConcurrentAsync`（NATS.Net 3.0.1 に存在、要 API 確認）を使い、ack を待つ `Task` を返す。
  サイズ検証・`EnsureStreamExistsAsync` は送信前に従来どおり行う。
- `GatewayIngressService.RunAsync`:
  - 容量 `GRPC_INGRESS_PUBLISH_WINDOW`（既定 `128`、`1` = 従来の直列）の `Channel<PendingFrame>` を持つ。
    読み取りループは publish を発行して ack Task を channel に入れる。満杯なら待つ（**背圧**。メモリは
    N × レコード上限 16 KiB で有界）。
  - ドレイン Task が channel を順に await し、成功なら `accepted++` + `published`、失敗なら `publish_failed`
    （例外種別は今日と同じ分岐）。stream 終了時（`MoveNext` が false）に channel を閉じ、ドレイン完了を待ってから
    `StreamAck` を返す。
  - `ct` キャンセル時: 未確定の ack は数えない（今日と同じ）。
- 既存の前段チェック（identity / run context / twin lookup / hierarchy）は変更なし。
- テスト: accepted は ack 済みのみ、ウィンドウ満杯で読み取りが止まる（背圧）、ウィンドウ途中の 1 件失敗で
  他は受理、キャンセル時に未確定分を数えない、`WINDOW=1` で既存テストがそのまま通る。統合テスト
  （`BuildingOS.IntegrationTest`、Testcontainers NATS）: stream 削除/容量超過で ack 失敗 → accepted 不一致なし。
- 期待効果: A と合わせて単一 stream 5k frame/s 以上（NATS 単体 pipelined 18.8k〜25k/s。以降は Worker CPU が
  律速になる見込みで、1 プロセス 1 stream で 5k は十分な余裕）。

### 4.3 スライス C（任意、Phase 2）: 連続 frame の 1 メッセージ化

- 同一 stream の連続 frame を `GRPC_INGRESS_BATCH_MAX_FRAMES`（既定 `1` = 無効）件または
  `GRPC_INGRESS_BATCH_MAX_MS` 以内でまとめ、`ValidMessage.telemetries` 配列として 1 publish。
  16 KiB のレコード上限を超えないよう事前にサイズで分割。
- A+B 実装後に S22 で効果（レート・Lake の行/ファイル比・NATS CPU）を測り、有意なら既定を変える。
  効果が小さければ実装しない。

### 4.4 スライス D: ドキュメントと運用値

- `docs/guides/gateway-onboarding-checklist.md`: 1 stream あたりの上限（改善前後の実測値）、高レート gateway は
  複数 stream（例: 1 stream ≒ 目標の 50% 以下）に分割、再送ポリシーとウィンドウ N の関係（§3.1）。
- `docs/architecture/telemetry-specification.md` §永続化保証: 「ack はウィンドウ内で非同期に待つが、`accepted` は
  ack 済みのみ」を追記。
- `docs/operations/oss-sla-freshness.md`: Hot KV の「ほぼ即時」に合体間隔（既定 100 ms）を脚注として追加。
- `CLAUDE.md` ConnectorWorker 環境変数表: `HOT_STORE_COALESCE_MS` / `HOT_STORE_COALESCE_MAX_POINTS` /
  `GRPC_INGRESS_PUBLISH_WINDOW`（/ `GRPC_INGRESS_BATCH_*`）。
- `docs/reference/ingest-throughput-sweep.md`: 改善後の再計測を §9 として追記。

### 4.5 受け入れ条件（#625）

- [ ] 同一環境（S22 §5 と同条件）で `--streams 1 --rates 5000` の達成率 ≥95%（G1）。最低ラインは 3,000 frame/s
      （A のみでも到達すべき値）
- [ ] `--streams 1,3,12 --rates 2000,5000,10000` で損失 0・重複 0・`accepted` = Lake 行数（探査 frame 除く）
- [ ] 既存 #187 テスト + 新規単体/統合テストが通る。`HOT_STORE_COALESCE_MS=0` かつ `GRPC_INGRESS_PUBLISH_WINDOW=1`
      で現行と同じ挙動
- [ ] Worker RSS がウィンドウ有界で頭打ちになる（24 stream・10k/s・60 s で現行最大 1.26 GB を大きく超えない）
- [ ] §4.4 のドキュメントが更新されている

### 4.6 リスク

| リスク | 緩和 |
|---|---|
| gateway 再送での重複増（§3.1） | N を小さく既定（128）、文書化、Lake 重複排除は別課題 |
| Hot KV 鮮度の遅れ | 既定 100 ms、`0` で無効化、鮮度メトリクスで可視化 |
| `PublishConcurrentAsync` の API 差（例外の出方・接続断時の挙動） | 統合テストで ack 失敗/接続断を再現。合わなければ `PublishAsync` を `Task` のまま保持する実装に切替（効果は同等） |
| ウィンドウ内 ack 順序と `accepted` の意味 | accepted は件数のみ（順序に依存しない）。ログは frame 単位を維持 |
| 単一 stream が速くなり Worker CPU が律速に移る | それが #626 で測る対象。CPU 律速なら `ingest` レプリカ増が効く |

## 5. #626 の検証計画（専用ホスト）

### 5.1 目的

G4: `WORKER_ROLE=ingest` のレプリカ数に対するスループットの線形性と律速箇所（NATS CPU / JetStream 書込み /
KV / ネットワーク / Worker CPU）を、負荷生成器と共有しない環境で確定する。

### 5.2 環境

最小 2 ノード、推奨 3 ノード。すべて同一 L2 または 1 Gbps 以上。

| ノード | 役割 | 目安 |
|---|---|---|
| infra | NATS（JetStream file storage）、RustFS、OxiGraph | 4 vCPU / 8 GiB、SSD |
| worker | ConnectorWorker `ingest` × 1/2/4（別ポート or 別コンテナ）、`lake` × 1（別プロセス） | 8 vCPU / 16 GiB |
| loadgen | `s22_ingest_rate_sweep.py`（`--procs` で複数プロセス） | 4 vCPU |

2 ノードの場合は infra + worker を同居させ、loadgen を分ける（S22 の最大の汚染要因は負荷生成器の CPU）。
Worker は Docker イメージ（`docker-compose.roles.yaml` を起点に `ingest` を複数定義）で動かし、各レプリカの
`GRPC_INGRESS_PORT` を分ける。gRPC は長寿命 stream のため **ロードバランサではなく `--ingress hostA:5051,hostA:5052,…`
でレプリカへ直接ラウンドロビン**する（LB 配下での再接続分散は本件の対象外、§7 に注記）。

### 5.3 マトリクス

各 60〜120 秒、判定は S22 と同じ（達成率 ≥95%、損失 ≤1%、重複 ≤0.5%、滞留 ≤5 秒分）。

| 系列 | 固定 | 振る | ねらい |
|---|---|---|---|
| R-1 レプリカ線形性 | 目標 20,000/s（頭打ちさせる）、`lake` 分離 | `ingest` 1/2/4 × stream 12/24/48 | レプリカあたり受理レートが一定か（線形）、どこで折れるか |
| R-2 ロール分離 | 12 stream、10,000/s | `all` 1 プロセス vs `ingest`+`lake` 分離 | JetStream 滞留（lake consumer lag）・`event_lag` p95 の差 |
| R-3 NATS 設定 | `ingest` 2、24 stream、頭打ちレート | JetStream `file`/`memory` storage、`sync` 間隔、（可能なら）3 ノードクラスタ R=3 | NATS 側の律速の寄与 |
| R-4 持続 | 目標の 70%、`ingest` 2 | 1〜4 時間 | RSS 増加、compaction、小ファイル |

各系列で記録するもの（既存 S22 出力に加えて）: レプリカごとの Worker CPU/RSS（`docker stats` または
`/metrics`）、NATS `/varz`（CPU・in/out bytes・slow consumers）と `/jsz`（stream bytes、consumer pending）、
KV put と publish-ack の p50/p99（`nats_bench.py` を同時刻に 1 プロセス走らせて基準値とする）、
`building_os.ingress.event_lag` p95、ネットワーク bytes/s。

### 5.4 律速の判定手順

1. レプリカを 1→2→4 にしてレプリカあたりの受理レートが **一定** → Worker 律速（スケールする）。
2. 合計が伸びず NATS CPU が 1 コア張り付き or `/jsz` の書込み遅延が増える → JetStream 書込み律速
   （R-3 でストレージ/クラスタの影響を確認）。
3. publish-ack は伸びるが KV put p99 が伸びる → KV 律速（#625 A の合体で緩和されるはず。前後比較で確認）。
4. NATS in-bytes がリンク容量に近い → ネットワーク律速。
5. いずれも余力があり伸びない → 負荷生成器律速（`--procs` を増やし再計測。loadgen の CPU を必ず記録）。

### 5.5 #625 との前後比較

#625 で 1 stream の上限が変わるとレプリカを飽和させるのに必要な stream 数が変わるため、**同じマトリクスを
2 回**走らせる。

1. **Baseline**: 現行 main（S22 時点のコード、`a24d719` 以降で ingress 未変更のコミット）。
   これは #625 と並行して**今すぐ始められる**。
2. **After**: #625 A+B マージ後。既定値と `HOT_STORE_COALESCE_MS=0` / `GRPC_INGRESS_PUBLISH_WINDOW=1` の
   両方で R-1 を取り、改善の寄与を分離する。

### 5.6 ハーネス変更（`Tools/e2e-performance`）

- `s22_ingest_rate_sweep.py`: `--ingress` 複数指定は既にある。追加するのは (a) レプリカごとの受理レート・CPU の
  記録（複数 `--worker-pid` または Docker コンテナ名）、(b) `--nats-monitor` から `/jsz` の採取、(c) 結果 JSON に
  環境メタデータ（ノード構成・コミット・環境変数）を入れる。
- `nats_bench.py` を `Tools/e2e-performance/` に移して正式ツール化（現在は data ディレクトリ）。
- 複数 `ingest` レプリカの compose 例: `docker-compose.roles.yaml` に `building-os.connector-worker-ingest-2`
  を追加するか、`--scale` 用にポートをレンジ指定できるようにする。

### 5.7 成果物と受け入れ条件（#626）

- [ ] `docs/reference/ingest-throughput-sweep.md` §9 以降に R-1〜R-4 の表・グラフ・律速箇所の結論（前後 2 回分）
- [ ] 生データを `docs/reference/ingest-throughput-sweep-data/` に追加
- [ ] SLO 候補の更新（基準 1,000 / 拡張 2,000 / 挑戦 5,000 frame/s を、レプリカ数・stream 数の条件付きで記述）
- [ ] `CLAUDE.md` の `WORKER_ROLE` 行と `docs/architecture/oss-tier-architecture.md` の「`ingest` は水平スケール」
      の主張を、計測で裏付けるか、制約付き（1 stream は 1 Worker 固定、LB 配下の再接続分散、NATS 単一ノード上限）
      に書き換える
- [ ] `kubernetes/keda/connector-worker-scaledobject.yaml` の見直し提案: 現在のトリガは `building-os-raw` の
      consumer lag で、gRPC ingress は raw を経由しないため **gRPC ingest のスケール信号になっていない**。
      CPU または gRPC の接続数/受理レートに基づくトリガへの置き換えを別 Issue として起票する

## 6. 全体の進め方とマイルストーン

```
      #626 Baseline（専用ホスト、現行 main）─────────────┐
                                                        ├─▶ 文書化（§5.7）
#625 A（KV 合体）─▶ #625 B（ウィンドウ）─▶ 再計測 S22 ─▶ #626 After ┘
                                   └─▶ #625 C（任意、効果次第）
                                   └─▶ #625 D（ドキュメント）
```

| M | 内容 | 完了条件 |
|---|---|---|
| M0 | 本 PRD の合意、目標値の確定（G1 = 5,000 frame/s/stream） | #625 / #626 の本文更新 |
| M1 | #625 A マージ | 単体テスト + `--streams 1 --rates 3000` PASS |
| M2 | #625 B マージ | `--streams 1 --rates 5000` PASS、統合テスト |
| M3 | #626 Baseline 計測 | §5.3 R-1/R-2 の表 |
| M4 | #625 D + #626 After 計測 + 文書 | §4.5 / §5.7 のすべて |
| M5 | #625 C の判断 | 効果の記録（実装する/しない） |

## 7. 文書化する運用上の制約（今回の調査で確定した事実）

- 1 本の gRPC stream は 1 つの Worker レプリカで処理される。高レート gateway は複数 stream に分ける。
- gRPC stream は長寿命のため、レプリカを**後から**増やしても既存 stream は移らない（再接続時のみ分散）。
- `StreamAck.accepted` は永続化済み件数。stream 断で未確定になる frame は最大ウィンドウ N 件。
- Hot KV の最新値は合体間隔（既定 100 ms）ぶん遅れうる。stale 判定（秒〜分）には影響しない。

## 8. 参照

- Issue: #625（1 stream の直列待ち）、#626（専用ホストでのスケール検証）、#187（ack 後受理）、#400 / #401（ロール分割）、
  #415（`event_lag`）、#447（複数レプリカの安全性）
- コード: `GatewayIngressService.cs`、`NatsIngressTelemetryBus.cs`、`ValidatedTelemetryHotStore.cs`、`NatsKvPublisher.cs`、
  `NatsKvLatestStore.cs`、`TelemetrySizeLimits.cs`
- 文書: `docs/reference/ingest-throughput-sweep.md`、`docs/architecture/telemetry-specification.md`、
  `docs/operations/oss-sla-freshness.md`、`docker-compose.roles.yaml`、`kubernetes/keda/connector-worker-scaledobject.yaml`
