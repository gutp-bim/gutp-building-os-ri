# インフラ可観測性（OTel/Prometheus）と Point 単位 Domain Health の責務境界

- **Status**: Proposed — maintainer 承認待ち。**新規の禁止事項を決める ADR ではない**。
  `docs/operations/observability-baseline.md` の Cardinality Policy として**既に運用中の方針を ADR に昇格**し、
  その裏側にある肯定的な判断（どちらの層が何を持つか）と、アラートの 2 段構成を確定する。
- **関連**: #459（本 ADR）、#450（PRD: 運用ストーリーを一本化する UI 情報設計）、
  #452（Phase 3: `GET /api/telemetry/health` server-side classifier）、
  #455（Phase 5: 永続 Health Event / ACK）、#456（`/platform/status` Pipeline KPI 拡張）、
  ADR-0004（到着=鮮度 / 接続=gateway / 値=アラームの 3 軸）、ADR-0005（アラームモデルと Phase 2b）

## Context

#450 は Operator / Admin / Platform / Observability の 4 層に UI を責務分離し、`/health`（Data Health）を
新設する。その前段として **「Point 単位の状態」をどこが持つか**を確定しておかないと、#452（server-side
classifier）・#455（Health Event）・#456（Platform KPI）の 3 つが別々の前提で実装される。放置したときの
具体的なリスクは「**stale な Point 数を Prometheus で見たい**」という自然で正当な要望から
per-Point metric が生えることで、これは後から剥がすのが難しい。

### 既存の記録が何を既に決めているか

`docs/operations/observability-baseline.md` §Cardinality Policy は**すでに**次を定めている（引用）。

- *"**Never use `point_id` or `device_id` as Prometheus labels.**"* — 理由として 100,000+ Point ×
  10 metrics = 1M active series を挙げ、point-level の詳細は Warm/Cold（Parquet）に、per-point の
  デバッグは Loki の**ログ行**（ラベルではなく）に置け、と指示している。
- *"**Fixed-vocabulary tags are fine.**"* — コード内の閉じた集合から来るタグは series 数が bounded なので
  可、として `building_os.ingress.messages{source,result,gateway}` / `ingress.event_lag{source}` /
  `ingestion.lag{subject}` の許容タグを表で列挙している。
- `building_os.ingress.event_lag` は **意図的に point / gateway タグを持たない**（テレメトリ実体ごとに
  記録するため、どちらを付けても per-point series が復活する）と明記されている。
  コード側も同じ理由をコメントに書いている（`BuildingOsMetrics.cs` の `IngressEventLag`）。

つまり**禁止事項は既に文書化・実装済み**である。本 ADR が足すのは、その裏返しである
**「では Point 単位の状態は誰が持つのか」**という肯定的な判断と、その帰結としてのアラート 2 段構成である。

### なぜ per-Point metric は「高い」だけでなく「静かに壊れる」のか

1. **series 数**。12,000 Point の運用（#450 が想定する規模）で `point_id` ラベルを 1 本入れると、
   metric 1 つあたり最低 12,000 series。freshness / alarm / gateway を `status` ラベルで割れば乗算になる。
   baseline の Baseline Targets は **Prometheus active series < 50,000 / memory < 512 MB** を目標値として
   置いており、`point_id` を 1 metric に入れるだけで予算の約 1/4、数本で即超過する。
2. **OTel SDK の cardinality limit で欠測ではなく「混入」になる**。OpenTelemetry .NET（本リポジトリは
   `1.17.0`）は metric stream あたり既定 2000 データポイントの cardinality limit を持ち、超過分は
   **エラーにならず** `otel.metric.overflow=true` の 1 データポイントに畳み込まれる。`OtelSetup.cs` は
   limit を明示設定していないので既定が効く。12,000 Point は 2000 を大きく超えるため、per-point 属性を
   足した metric は「重い」のではなく「**一部の Point だけが正しく、残りは overflow に混ざった値**」に
   なる。可観測性の signal としては壊れている。
3. **Prometheus は既定スタックに無い**。観測系は `--profile observability` のオプトインであり
   （CLAUDE.md / baseline §Minimum Profile）、`PROMETHEUS_URL` 未設定時は KPI が null に degrade する
   設計である。**UI の基本機能を任意コンポーネントに依存させられない**という判断は ADR-0004 が
   gateway connected 状態で既に下している（オプション 3「Prometheus から逆算」を却下）。Point の
   freshness/alarm を Prometheus に置くと、観測プロファイル無しの既定構成で `/home` も `/health` も
   成立しなくなる。

### 一方で、Point 単位の状態はドメイン機能として完結する

「どのビル・設備・Point がおかしいか」は Building OS の主要ユースケースであり、判定に必要な材料は
**すべてサーバ側に揃っている**（#452 の調査）。

| 材料 | 出典 |
|---|---|
| Point inventory / metadata / `sbco:interval` / `customTags` / gateway / device / building | OxiGraph（`OxiGraphDigitalTwinDatabase` の共有 `PointVars` projection） |
| 最新値 + timestamp | NATS KV `telemetry-latest`（`NatsKvLatestStore.cs`） |
| 鮮度閾値の既定 | `SettingsRegistry`（`telemetry.staleThresholdSeconds` / `telemetry.staleIntervalMultiplier`） |
| アラーム閾値 | twin `bos:alarmHigh/Low` [+ `warnHigh/Low`]（ADR-0005） |
| Gateway 接続状態 | NATS KV `gateway-connection`（ADR-0004） |

判定そのものも既に純関数として存在する（`web-client/src/lib/telemetry/freshness.ts` の
`classifyPointFreshness`、`web-client/src/lib/home/aggregate.ts` の `buildAttentionList`）。
つまり Point 単位の状態は **twin × KV の join** で閉じており、時系列データベースを必要としない。

### アラートの現状

`oss-stack/prometheus/recording_rules.yml` に recording rule はあるが、**alerting rule も Alertmanager も
現状のスタックには無い**（`oss-stack/loki/loki-config.yaml` の `alertmanager_url` は Loki ruler の既定
プレースホルダで、実体は配線されていない）。したがって本 ADR の「2 段構成」の上段は**将来の置き場所の
予約**であり、実装は本 ADR の対象外（#459 非目標、`observability/` 側の別 Issue）。

## Considered Options

| 案 | 内容 | 評価 |
|----|------|------|
| **A. すべて Prometheus に寄せる** | Point の freshness/alarm も metric として出し、UI は PromQL を叩く | ❌ 上記 Context の 3 点（series 予算・SDK の overflow による静かな破損・Prometheus がオプトイン）。**不採用** |
| **B. すべてドメインに寄せる** | pipeline 健全性も Building OS の API で自前集計し、OTel/Prometheus を使わない | ❌ rate / histogram quantile / 保持 / alert routing は観測基盤の得意分野で、再実装は重複かつ劣化。既存 instrument 資産（`BuildingOsMetrics.cs`）と recording rule を捨てることになる。**不採用** |
| **C. 層で分ける（採用）** | 母数が**コンポーネント数**の情報 = OTel/Prometheus、母数が **Point 数**の情報 = Building OS ドメイン | ✅ 既存 baseline の禁止事項と整合し、既存資産をどちらも活かす。境界の判定基準が「series の母数が何に比例するか」という機械的な一線になる |

→ **C を採用**。

## Decision

### 1. 情報 → 保存/計算 → 表示の対応

| 情報 | 保存・計算 | 表示 |
|---|---|---|
| ingress / connector msg/s、rejected（`result` 別）、event lag、consumer lag、Parquet writer lag、NATS backlog | **OTel / Prometheus**（低カーディナリティ属性のみ） | Platform `/platform/status`（#456）/ Grafana |
| Point の lastSeen / freshness / alarm、missing 原因分類 | **Building OS domain**（twin(OxiGraph) × NATS KV の join、#452） | Operator `/home` / `/health` |
| Point の tags / expectedInterval / 閾値 | **OxiGraph / Twin** | `/resources` / `/points/{id}` |
| health event の raise / clear / ACK / 履歴 | **PostgreSQL**（#455、`point_control_audit` と同じ共有 DB） | `/health` |

**境界の判定基準**: その signal の series 数（あるいは行数）が **コンポーネント数に比例するなら OTel**、
**Point 数に比例するならドメイン**。この一線で迷わないこと。

### 2. OTel 属性のルール

- **`point_id` / `device_id` を metric 属性に入れない**（baseline の再確認。新設ではない）。
- **許容するのは低カーディナリティ属性のみ**。既存 instrument が使っているのは
  `connector` / `source` / `protocol` / `subject` / `result` / `handler` / `tier` / `gateway` で、
  いずれも **コード内の閉じた集合**か、**数十〜数百に bounded な運用単位**（`gateway`）である。
  追加する属性も同じ性質に限る。`building` は建物数が数百を超える運用なら**外す**。
- **Point 数に依存しない集計値なら OTel に出してよい。** 例えば
  `building_os.health.points{freshness=stale}` のような gauge は series 数が状態値の数で決まり、
  Point 数に依存しない。これが「stale な Point 数を Prometheus で見たい」への正しい答えである
  （#452 の classifier サマリからの export）。ただし **building 別に割ると建物数倍になる**ので、
  既定は建物属性なしとする。
- **判定ロジックの正本は 1 つ**。上記 gauge は #452 の classifier の**サマリを export するだけ**で、
  閾値判定を OTel 側に二重実装しない。

### 3. アラートの 2 段構成

| 段 | 評価者 | 対象 | 性質 |
|---|---|---|---|
| **インフラ** | Prometheus alerting rule + Alertmanager（**未配線・将来**） | API Server down / NATS unavailable / ingestion lag > 閾値 / Parquet writer failure / disk capacity | 対象は**コンポーネント**で個数が有限。**Building OS 自身が落ちていても鳴る**（自己監視の独立性） |
| **ドメイン** | Building OS Health Event（#455、常駐評価器 + PostgreSQL） | Point missing / Point stale / 閾値逸脱 / gateway offline | 対象は **Point** で数万規模。ACK・履歴・storm 抑制というドメイン運用が要る。**Building OS が動いていないと鳴らない** |

この非対称が 2 段に分ける理由そのものである。ドメイン alert は「Building OS が生きている」ことを前提に
するので、**「Building OS 自身が死んだ」は必ず上段が持つ**。逆に「Point が missing」を上段に持たせると、
Alertmanager 側のラベルに `point_id` が生えて同じ cardinality 問題が再発する。

### 4. Grafana の位置づけ

**Grafana を管理画面の代替にしない。** Building OS UI が Point → Device → Gateway までの到達性と
「なぜ異常か」の説明責任を持ち、Grafana へは **deep-link 先**として繋ぐ（`GRAFANA_URL` 未設定なら
非表示）。これは「Grafana 任意化」（`docs/project/oss-unified-console-roadmap.md` Epic B）の趣旨であり、
観測プロファイル無しの既定 OSS 構成でも運用が成立することを意味する。

## Consequences

- **#452 がサーバ側 classifier + ページング検索になることが確定する。** 「stale だけを数万 Point から
  絞る」を Prometheus に逃がす選択肢が閉じるため、twin × KV の join をサーバ側に持ち、`/health` は
  その API を叩く。ブラウザ全走査（現行 `/home` の `loaders.ts`）はスケール上限がある実装として扱う。
- **#456 の「新しい instrument は追加しない」という制約が正当化される。** Platform KPI は既存
  instrument と既存 recording rule の露出であり、KPI が増えるたびに instrument を足す運用にはしない。
- **#455 の Health Event が PostgreSQL に行く根拠が明示される。** raise/clear/ACK/履歴は Point 数に
  比例する行であり、時系列 metric ではなく関係データである。
- **観測プロファイル無しの既定構成でも `/home` / `/health` は完全に機能する。** 逆に
  `/platform/status` の Pipeline KPI は従来どおり null に degrade する（既存挙動の追認）。
- **`BuildingOsMetrics.cs` への instrument 追加 PR は、属性の cardinality を自己申告する。**
  申告先は baseline の許容タグ表であり、判断の根拠として本 ADR を参照する。
- **baseline と ADR の役割を分ける。** baseline は**運用値・設定の記録**（許容タグ表・保持期間・
  Baseline Targets・recording rule 一覧）に専念し、**判断の記録**は本 ADR に寄せる。内容は移さず、
  baseline から本 ADR へリンクするだけにして重複を作らない。
- **ADR-0004 / ADR-0005 の 3 軸（到着=鮮度 / 接続=gateway / 値=アラーム）はそのまま維持される。**
  本 ADR は「どの層が持つか」を決めるだけで、軸を潰さない（#452 も `freshness` / `alarm` / `gateway`
  の独立軸 + 派生 `healthStatus` という形でこれを守る）。

## Open Questions（承認時・実装前に確定）

1. **`building_os.health.points` 集計 gauge の導入時期と形**（#452 の classifier サマリから export する
   前提で、instrument 名・属性の閉集合・`building` 属性を許すかの棟数しきい値）。本 ADR は「出してよい」
   と決めるだけで、追加は #452/#456 の実装判断に委ねる。
2. **Alertmanager の導入そのもの**（現状スタック未配線）と、alerting rule の置き場所
   （`oss-stack/prometheus/` か `observability/prometheus/` か）。本 ADR の対象外だが、上段が空のまま
   だと「インフラ側の異常が誰にも通知されない」状態が続く。
3. **2 段の重複排除**。gateway offline は上段（bridge プロセスの死）と下段（gateway の切断）の両方で
   鳴りうる。通知ポリシー（#162）で吸収するのか、ドメイン側を抑制するのか。
4. **`gateway` 属性の上限**。数百を超える運用が出た時点で、`gateway` も per-point と同じ問題に近づく。
   どこで切るか（baseline の許容タグ表に上限を明記するか）。
5. **PR テンプレートへの cardinality 自己申告欄**を実際に追加するか（テンプレート変更は本 ADR 外）。

## 参照

- `docs/operations/observability-baseline.md` — §Cardinality Policy（本 ADR が昇格した既存ポリシー本体。
  許容タグ表・Baseline Targets・recording rule 一覧は引き続きこちらが正）、§Ingest Lag Signals（#415）
- `docs/operations/oss-sla-freshness.md` §5–§6（鮮度 KPI と取り込み飽和の読み分け）、§7（#183 期待周期ベースの
  Point 別鮮度判定）
- ADR-0004（`docs/adr/0004-gateway-connected-state-heartbeat.md`）— 「UI の基本機能を任意の観測基盤に
  依存させない」判断の先例（Prometheus 逆算案の却下）、および 3 軸の分離
- ADR-0005（`docs/adr/0005-alarm-model.md`）— derived-on-read（Phase 2a）と Phase 2b イベントライフサイクル。
  #455 はその具体化
- `docs/project/oss-unified-console-roadmap.md` Epic B（Grafana 任意化）
- コード: `DotNet/BuildingOS.Shared/Infrastructure/Telemetry/BuildingOsMetrics.cs`（instrument 一覧と
  `IngressEventLag` のタグ非付与コメント）、`OtelSetup.cs`（meter 登録、cardinality limit は既定のまま）、
  `NatsKvLatestStore.cs` / `NatsKvGatewayConnectionStore.cs`（ドメイン側の状態源）、
  `web-client/src/lib/telemetry/freshness.ts` / `web-client/src/lib/home/aggregate.ts`（既存の判定純関数）、
  `oss-stack/prometheus/recording_rules.yml`（recording rule、alerting rule は未配線）
