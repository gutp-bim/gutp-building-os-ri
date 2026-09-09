# SLA / 鮮度モデル — テレメトリの「いつ読めるか」

Building OS のテレメトリ読み取りは Hot / Warm / Cold の 3 層を `GET /telemetries/query` が自動選択します
（[oss-tier-architecture.md](../architecture/oss-tier-architecture.md)）。層によって**鮮度（イベント発生から読めるまでの遅延）**
が異なるため、運用者・利用者が「どのクエリは即時で、どのクエリは何分遅れるか」を判断できるよう本書にまとめます。

> ⚠️ 本ソフトウェアは現状有姿（AS IS）・無保証です。下記は**設計上の鮮度モデルと目標値**であり、契約上の
> SLA を保証するものではありません（[免責事項](../README.md#免責事項-disclaimer)）。実値は環境・負荷依存で、
> 本番スケールの確定は専用ベンチでの実測（[#297](https://github.com/takashikasuya/gutp-building-os-oss/issues/297)）が前提です。

---

## 1. 一目でわかる鮮度モデル

| 読み取り | 経路 | 鮮度（イベント→読める） | 目標 p95 レイテンシ | 用途 |
|---|---|---|---|---|
| **最新値** `latest=true` | Hot KV（NATS KV `telemetry-latest`） | **ほぼ即時**（publish 直後に上書き）[^visibility] | < 20ms（実測 latest API p95 ~7–60ms[^m]） | リアルタイムダッシュボード |
| **直近レンジ**（end が現在に近い） | Warm（Parquet レイク）＋ **tail-merge**（#220） | flush 未到達分を JetStream から補完し**ほぼ即時** | < 2,000ms（実測 warm p95 ~55ms） | 直近のチャート末尾 |
| **過去レンジ**（end が flush 済み区間） | Warm/Cold（Parquet レイク） | **flush 間隔ぶん遅延**（既定 5 分） | < 2,000ms（warm）/ < 5,000ms（cold） | 履歴チャート |
| **集計**（Hour/Day 粒度） | rollup（agg_hourly）＋欠落のみ on-read | rollup 生成（compaction）の進行度に依存 | < 3,000ms（実測 agg_hour ~606ms, rollup-backed） | 長期トレンド |

[^m]: 計測条件（ローカル/QUICK）により latest API p95 は 7ms（E3 freshness 専用ハーネス）〜60ms（S5 エンドポイント全体）。
  いずれも目標を大きく下回る。生値は [PERFORMANCE_SUMMARY](../../Tools/e2e-performance/PERFORMANCE_SUMMARY.md) / [evaluation-summary.md](../reference/evaluation-summary.md)。
[^visibility]: 「ほぼ即時」は当該点が twin の Point List に**継続して可視**だった場合の話（#417）。ある点が
  一時的に非公開化され、その間の送信元 publish が gateway に point-list miss として捨てられていた場合、
  再公開後の `latest=true` は非公開化「前」の保存済み行を返しうる——`datetime` が取り込み時刻より古くなり
  うる。値の新鮮さは応答の `datetime` を実際に見て判定すること（レイテンシは低いままでも、返る値自体が
  古いことがある）。詳細は `GET /telemetries/query` の `latest` パラメータの API ドキュメントを参照。

**要点**: 「最新値」と「直近レンジ末尾」は即時。「過去レンジ（flush 済み区間）」だけが flush 間隔ぶん遅れます。

> ⚠️ **この表は「取り込みが飽和していない」ことを前提にしています。** 投入レートが環境の持続可能レートを
> 超えると、Hot KV の「ほぼ即時」という前提そのものが崩れます——レイテンシは低いまま、返る値の `datetime` が
> 現在から数分〜十数分ずれていく、という形で（[#415](https://github.com/gutp-bim/gutp-building-os-ri/issues/415)）。
> 検知の仕方と自環境の上限の測り方は **[§6](#6-取り込み飽和と自環境の持続可能レートの測り方415)** を参照。

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
（[oss-parquet-tail-merge.md](../architecture/oss-parquet-tail-merge.md)）。

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
| `ingress.event_lag`（histogram, s, tag: `source`）**#415** | 受理した値の**イベント時刻**（`datetime`）から Hot KV 到達までの秒数。取り込みが飽和すると**これだけが上がる**（値は届き続けるので他は正常に見える） | 定常負荷で p95 が横ばい。**単調増加＝飽和** |
| `ingestion.lag`（histogram, s, tag: `subject`）**#415** | `raw.*` の JetStream メッセージのストリーム時刻からコネクタが取り出すまでの秒数（consumer lag） | 横ばい。増加＝NATS 以降が追いつけていない |
| `ingress.messages`（counter, tags: `source`, `result`）**#415** | 取り込み経路ごとの受理/拒否件数。`source=mqtt\|amqp` は `published` / `bad_topic` / `bad_payload`、`source=gateway-grpc` は #292 の拒否理由 | 拒否理由が継続的に増えない |
| `ingress.timestamp_fallbacks`（counter, tags: `source`, `gateway`）**#418** | timestamp 欠落/不正で受信時刻へフォールバックした件数 | 0 に近い（下記の注記も参照） |
| `parquet_writer.freshness_lag`（histogram） | flush 時の `now − max(event time)` | p95 ≤ flush 間隔 + 60s |
| ParquetLakeWriter の consumer pending（gauge） | writer がレイクへ追従できているか | 持続負荷で**単調増加しない** |
| `parquet_lake.tail_merge_rows` / `tail_merge_errors`（counter） | tail-merge の補完行数 / デグレード回数 | errors が継続的に増えない |

> ⚠️ **`ingress.event_lag` は必ず `ingress.timestamp_fallbacks` と併読すること。** timestamp が欠落/不正な
> フレームは受信時刻へフォールバックする（`GatewayIngressService.NormalizeTimestamp` / `IoTIngressConnectorBase.ExtractTimestamp`,
> #418）。そのフレームのイベント時刻は「たった今」になるため、**event_lag は構造的にほぼ 0 として記録されます**。
> つまり `timestamp_fallbacks` が立っている割合ぶんだけ event_lag は遅延を**過小評価**します。fallback が増えて
> いる状態の event_lag は「遅れていない証拠」にはなりません。

> **`ingestion.lag` と `event_lag` の違い**: `ingestion.lag` は「NATS に載ってからコネクタが取り出すまで」だけを
> 見るので、詰まりが NATS より**上流**（ゲートウェイ側の送信バッファ等）にある場合は横ばいのままです。
> `event_lag` はイベント時刻起点なので上流の滞留も含めて上がります。**両方を並べて見ると詰まりの位置が分かります**
> （両方上がる＝Building OS 内部が律速、event_lag だけ上がる＝上流が律速）。

上記は [oss-warm-parquet-lake.md](../architecture/oss-warm-parquet-lake.md) / [observability-baseline.md](observability-baseline.md) に定義。
本番スケールでの実測は [#297](https://github.com/takashikasuya/gutp-building-os-oss/issues/297)。

---

## 6. 取り込み飽和と「自環境の持続可能レート」の測り方（#415）

§1–§5 は「詰まっていない前提」での鮮度モデルです。本節は**その前提が崩れたとき**——取り込みが飽和して、
値は届き続けるのに中身がどんどん古くなる状態——を、どう検知し、どう自環境の上限を測るかを扱います。

### 6.1 まず: drop 数は Building OS 側からは原理的に出せない

[#415](https://github.com/gutp-bim/gutp-building-os-ri/issues/415) の報告事象（`received 35.2/s` に対し
`accepted 13.2/s`、`dropped 35.1/s`、`buffer_depth` 増加）で実際に値が捨てられているのは、**ゲートウェイ
プロセス側の送信バッファ**（nexus-gateway の MQTT クライアントバッファ）です。そこで捨てられた値は
Building OS のプロセスに**到達しません**。到達しないものを数えることはできないので、
**「Building OS の drop カウンタ」は原理的に提供できません**（提供したとしても常に 0 で、まさに嘘をつく指標に
なります）。

そこで #415 への回答は次の代替です:

| 知りたいこと | 見る指標 | 根拠 |
|---|---|---|
| 送信元で捨てられているか | ゲートウェイ側の `dropped` / `buffer_depth` | 捨てている当人しか数えられない |
| 取り込みが遅れているか（＝上の副作用） | **`ingress.event_lag` の単調増加** | 飽和すると受理済みの値のイベント時刻が現在から離れ続ける |
| 詰まりが Building OS 内部か上流か | `ingestion.lag` を併読 | §5 の注記参照 |
| Building OS 内部で捨てた分 | `ingress.messages{result!="published"}` | 形式不正（`bad_topic` / `bad_payload`）と #292 の拒否理由 |

`ingress.messages` の `bad_topic` / `bad_payload` は **#415 が報告した drop とは別物**です（Building OS が
自分で拒否した件数であって、送信元バッファ溢れではありません）。混同しないでください。

### 6.2 飽和の判定

以下が同時に成り立っていれば飽和と判断してよいです。

1. `ingress.event_lag` の p50/p95 が**時間とともに単調増加**する（横ばいなら遅いだけで飽和ではない）
2. `ingestion.lag` も同様に増加する、または `nats:jetstream_consumer_pending:max`
   （recording rule, `oss-stack/prometheus/recording_rules.yml`）が増え続ける
3. `ingress.timestamp_fallbacks` が増えて**いない**（増えている場合は 1 の値が過小評価なので、上げ幅で
   判断してはいけません）

エラー率・HTTP ステータス・`normalizer_invalid_total` は**どれも動きません**。それが #415 の核心で、
飽和は「エラー」ではなく「遅延」としてしか現れません。

### 6.3 自環境の持続可能レートの測り方

**この数字は環境ごとに違います。** #415 の報告にある 13 点/s は、報告者の環境（macOS / Docker Desktop、
全 15 コンテナを 1 台に同居、専用ハードなし）での実測値であり、**Building OS の仕様値でも保証値でもありません**。
専用ノード、SSD 実効性能、コンテナ分離の度合いで大きく変わります。自分の環境の上限は次の手順で出してください。

1. **投入レートを段階的に上げる。** 例: 130 点を 60 / 30 / 20 / 10 / 5 秒周期（≒2.2 / 4.3 / 6.5 / 13 / 26 点/s）と
   段階を切り、各段階を**最低 10 分**維持する。短時間ではバッファに吸収されて上限が見えません。
2. **各段階で §6.2 の 1〜3 を確認する。** `event_lag` の p95 が段階の終わりにかけて上がり始めた最初の段階が上限です。
   その**ひとつ手前**の段階を持続可能レートとして採用してください（余裕込み）。
3. **理論上の直列上限を目安にする。** `building_os_connector_process_duration`（ms, histogram）の p50 の逆数が、
   1 subject あたりの直列処理の理論上限です（`NatsMessageSubscription` は 1 件ずつ handler → Ack を直列実行し、
   subject あたりの並列度は常に 1）。実測の上限がこれに近ければ律速はコネクタ処理、大きく下回るなら
   NATS / KV / ディスクなど外側を疑ってください。
4. **測定結果に「そのとき飽和していたか」を必ず添える。** 飽和状態で測ったレイテンシは
   レイテンシではなく**キュー待ち時間**です。#415 の報告者は最初の測定一式をこの状態で取っており、
   別の検出器がたまたまタイムアウトするまで気づけませんでした。ハーネス側で各測定に飽和フラグを
   立てておくことを強く推奨します。

### 6.4 飽和したときの緩和策

本リリースは**観測可能にするところまで**で、スループット自体の改善（直列 consume ループの並列化、
per-message KV put のバッチ化など）と背圧機構は含みません。現状取れる手は運用側の調整です。

| 手 | 効果 | 副作用 |
|---|---|---|
| 投入レートを持続可能レート以下に落とす | 確実 | 時間分解能が落ちる |
| 点数を分割して複数ゲートウェイ／複数 subject に分ける | subject 単位で直列なので並列度が上がる | 構成が増える |
| ConnectorWorker を `WORKER_ROLE=ingest` で水平分割する | ingest はスケールアウト可（`lake` は単一レプリカ） | オーケストレーション必要 |
| 同居コンテナを減らす／専用ノードへ移す | 報告環境のように 15 コンテナ同居だと I/O 競合が支配的 | インフラコスト |

---

## 7. Point 別「鮮度切れ」判定 — 期待周期ベース（#183）

上記 §1–§6 は「イベント→読める」までの**配信**鮮度。本節は運用ダッシュボード（オペレータ ホーム
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

## 8. 関連

- [oss-tier-architecture.md](../architecture/oss-tier-architecture.md) — Hot/Warm/Cold 階層と Query Router
- [oss-warm-parquet-lake.md](../architecture/oss-warm-parquet-lake.md) — 既定の Parquet レイク（背景/構成/KPI）
- [oss-parquet-tail-merge.md](../architecture/oss-parquet-tail-merge.md) — tail-merge 設計
- [evaluation-summary.md](../reference/evaluation-summary.md) — E2E 実測と妥当性
- [oss-production-deployment.md](oss-production-deployment.md) — 本番デプロイ構成
