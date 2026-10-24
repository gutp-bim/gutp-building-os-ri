# RI #606: ClockSource設計・契約fixture案

本ディレクトリは設計と契約fixtureを管理する。RI側の静的設定取込み・配布を実装した。
現在の動作と検証範囲は[実装仕様](../../architecture/oss-gateway-clock-provisioning.md)を参照。
他リポジトリ・稼働環境への投入は行っていない。
[ADR案](../../adr/0009-equipment-clock-source.md)を入口とする。
新APIはdraftとして実装、語彙の横断承認とrun contextの実装は後続。ここは共通ontologyの正本ではない。

## 既存実装の確認

調査日2026-10-24。RI checkout `7a298fe68276c56985c25d1510907d52c2915ab6`。
以下は実ファイルを確認した結果であり、Issue記載だけから実装済みと判断していない。

| 入口 → 処理 | 確認結果 / 設計への影響 |
|---|---|
| `DotNet/BuildingOS.ApiServer/Controllers/GatewayProvisioningController.cs` → `GatewayProvisioning/GatewayPointListResponse.cs` / `GatewayPointListSerializer.cs` | gateway所有点のDTO、ETag、since diff、cache。hash対象はPointのみ。clock専用revisionなし |
| `DotNet/BuildingOS.Shared/Infrastructure/OxiGraph/OxiGraphDigitalTwinDatabase.cs` `ListGatewayPointList` → `OxiGraphOntology.cs` | `sbco:PointExt`とgatewayIdで点を抽出、Equipment情報をjoin。既存bos namespaceあり、ClockSource定数/取得契約なし |
| `proto/gateway_ingress.proto` → `DotNet/BuildingOS.ConnectorWorker/Connectors/GatewayIngressService.cs` | attributesは既存string map。gatewayId以外はdataへ転記。timestampは取得実UTCとして扱えるがsim語彙の型/関係検証はない |
| `DotNet/BuildingOS.Shared/Infrastructure/Telemetry/ParquetLake/ValidTelemetryEnvelope.cs` → `ParquetTelemetrySerializer.cs` | dataのJSONを保持してParquet data列へ保存。数値sim時間の列/索引/集約契約を保証しない |
| `DotNet/BuildingOS.ApiServer.Test/GatewayProvisioning/{GatewayPointListSerializerTest,PointListMaterializerTest,PointListDifferTest}.cs`、`DotNet/BuildingOS.Shared.Test/Infrastructure/OxiGraph/ListGatewayPointListTest.cs`、`Infrastructure/ConnectorWorker/GatewayIngressServiceTest.cs` | 既存Point配布/Ingressの回帰検証入口。今回製品テストの追加・実行はしていない |

他repoは読み取りのみ、Issue本文・コメントと次の固定版ファイルをGitHub appで確認した。

- [Builder rdf.ts @ 3f564c0](https://github.com/smartbuilding-co-creation-organization/smartbuilding_datamodel_builder/blob/3f564c087d1131116b8b8ff0f29443a18f0ecd8f/packages/core/src/rdf.ts):
  REC以外のクラスはsbcoへ、未知述語はsbco propertyへ割当て。ClockSourceをkind追加するだけではbos語彙にならない。
- [Nexus ADR-0003 @ bf54683](https://github.com/gutp-bim/nexus-gateway/blob/bf54683a23ea3083676e212e9d7bf6e5d1d823c7/docs/adr/0003-point-list-source-of-truth.md):
  RI正本、discovery proposal-only、file/url/fallback、一方向promotion。
- [Nexus BACnet event.py @ bf54683](https://github.com/gutp-bim/nexus-gateway/blob/bf54683a23ea3083676e212e9d7bf6e5d1d823c7/connector/bacnet/bacnet_connector/event.py):
  Python CommonEvent/make_eventにattributes引数なし。Go経路に属性があってもBACnetからの転送完了とはいえない。
- [RI #606](https://github.com/gutp-bim/gutp-building-os-ri/issues/606)、
  [Builder #44](https://github.com/smartbuilding-co-creation-organization/smartbuilding_datamodel_builder/issues/44)、
  [Nexus #201](https://github.com/gutp-bim/nexus-gateway/issues/201)の本文とコメントも確認。
  chunked RPM/COV/Store-and-Forward/BDEの記述はIssue由来の設計制約であり、今回実機・全経路の再検証はしていない。

## Fixtureの読み方とRDF→JSON対応

`fixtures/model.ttl`は設備2・設備Point35（31 read-only / 4 writable）・clock 1・設備参照2。
複数設備が1時計を共有する例。実bestest_airの設備分類/値/アドレスを再現するものではない。
すべて合成データで、native device 1234や各Pointのinstance/unit/control範囲も実機に使用不可。
ID範囲だけは合意されたBPT-0002〜0036を維持する。BPT-0001は含まない。

| RDF案 | `provisioning.json`案 |
|---|---|
| ClockSource subject IRI | clockSources[].clockId |
| clockDomain / clockUnit / readOnly / acquisitionMode | clockDomain / unit / readOnly / acquisitionMode |
| nativeBinding → gatewayId / connectorId / deviceInstance / objectType / objectInstance / property / nativeUnit | native.gatewayId / connectorId / deviceInstance / objectType / objectInstance / property / unit（protocolはbacnet固定） |
| Equipment subject IRI → usesClockSource IRI | clockBindings[].equipmentId → clockId |
| Equipment hasPoint | points[].device.dtId（equipment IRI）とpoint所属の展開。BACnet deviceIdとは別物 |
| Point sbco:id / native / writable等 | 既存GatewayPointDto形式のpoints[]。clockを点として追加しない |

`fixtures/clock-shapes.ttl`は追加部分のSHACL案で、完全な既存SBCO/REC schemaの代わりではない。
適用対象は新clock契約へ明示的に参加するgatewayの候補graph（参照先を含む）とし、
既存clockなしRDFへこの追加shapeを一律に適用して新しい所属制約を課さない。
対象class/IRI/必須値/一意参照/所有gateway/点の複数所属/廃止ID/二重型付けを検証する。
clock/nativeはclosed shapeでrun contextや未知設定を拒否する。
重複IRIの同一tripleはRDF集合では重複を表せないため、Builder入力の重複行とJSONの重複clock IDは
RDF生成前/RI API境界でも別途拒否する。IRIが同じで値が異なる場合はmaxCountで拒否する。
SHACLだけでは実際のBACnet object存在・読取権限・単位一致を検証できない。Nexus apply時のprobeが必要。

`fixtures/provisioning.json`は新GET候補の200本文。camelCaseは既存RI DTOに合わせた案。
ETagは本文revisionと同じ値、Cache-Control: private, no-cache。認可前に304を返さない。
未知schemaVersion/必須項目/enum/重複ID/参照不整合はapply前に拒否する。
HTTP例: `GET /gateways/GW-EXAMPLE/provisioning/v1`、次回は`If-None-Match: <revision値>`。
このURLはRIに実装したdraft API。nullプロパティは本文/hashの両方から省略する。

## 静的snapshot・取得品質・run context

時刻契約の判断は[ADR-0010](../../adr/0010-simulation-observation-time-contract.md)にまとめた。
Nexus参照SHAは`bc1a9e5137fec041df250e4f927299eaeb5d9a0f`。
指定版のprototypeは4品質とsim_clock_revisionを出すが、run/stepを生成しない。

| fixture | 役割 |
|---|---|
| `telemetry.json` | 新形式のstable_unfenced + absent。正常な診断観測で、run ID/generationを持たない |
| `telemetry-with-run.json` | stable_unfenced + valid。独立した認証・取得区間・commit履歴の証拠が揃うテストでのみ有効 |
| `run-context.json` | clock-run-context/2-draft。issuedAtとvalidFromを区別。取得方式のconsistencyModeは含めない |
| `acquisition-evidence.json` | poll開始/終了、chunk数、各境界clock値、resetラッチ、context切替を表すテスト入力。送信属性ではない |
| `receiver-evidence.json` | 発行元認証・権限・復旧済み世代履歴・commit/適用時刻・無効化runのテスト入力。認証/永続化の実装ではない |
| `time-contract-cases.json` | 受入/拒否scenario。frame/contextファイルを読み、attributes/omitAttributesと各Patchを適用して独立した期待値と比較する |
| `compat/ri-6e0fefe-*.json` | 変更前RIのtelemetry/context原文。bracketedや旧run主張を診断用に残す |
| `compat/nexus-bc1a9e5-telemetry.json` | 指定Nexus版のwire属性を使う合成RI frame例。Nexus Common Eventのnative IDをそのままRIに送れることを意味しない |

telemetry fixtureはprotoフィールド名で示した読解用frame例。attributesは既存map<string,string>。
静的provisioning/RDF・設備35点は変更しない。context現在値やrun IDを静的graphへ書かない。

| 属性 | 制約 |
|---|---|
| sim_clock_id | 適用関係で明示されたclock IRI。gateway/pointのscopeと照合 |
| sim_config_revision | 取得時のsnapshot全体のrevision。新producerのcanonical名 |
| sim_clock_revision | 既知のNexus旧形式だけで読むalias。同時に異値なら拒否。元イベントは書き換えない |
| sim_time_quality | 新producerはstable_unfenced / changed / missing / reset。run有効性と独立 |
| sim_time_s | stable_unfencedで必須、他の新品質では禁止。非負・有限の数値文字列。指数表記も許容しNaN/Infinity・空白・先頭ゼロ等を拒否 |
| sim_run_status | absent / valid / invalid / unverified。送信されたvalid文字列自体を信用しない |
| sim_run_reason | invalid/unverifiedで必須。その他では禁止 |
| sim_run_id / sim_context_generation | 新形式ではvalidの場合だけ必須。generationはcontextの正整数を10進文字列で表す |
| sim_step_id | 未対応。poll回数や固定stepから生成せず、現在の検証では拒否 |

stable_unfenced + absent/invalid/unverifiedはsim秒を診断注記として保持してよいが、run別sim時間集約・joinから除外する。
changed/missing + validではrun識別を保持できるがsim秒は出さない。
run内の数値時間解析候補はstable_unfenced + validのみ。それでも厳密step対応を保証しない。
旧bracketedは全chunk検査の証拠へ格上げせず、旧unavailableの原因も推定しない。
unknown runという共有runへまとめない。UTC軸の設備値閲覧は引き続き可能。

run contextの有効性は取得区間全体・取得開始以前のcommitと適用・scope/revision・取消・世代履歴・reset無効化を照合する。
同世代同内容は冪等、異内容・過去世代は拒否。上位世代の受信だけでは有効化しない。
reset後は新run IDと上位世代を要し、restartで状態を復旧できなければunverifiedのままとする。
`time_contract.py`の世代遷移検証は純粋関数で、durable commitや初期watermark確立を行わない。

取得品質はpoll前と各chunk後の有効なclock/単位検査で判定し、reset > missing > changed > stable_unfencedの順。
証拠fixtureのclockSamplesのnullは欠損/不正値/単位検査失敗等を表す。前回値へフォールバックしない。
再送は取得時のsnapshot/context/stateを使い、現在の設定や現在時刻へ付け替えない。
入力fixtureを変更せず、互換解釈の結果を別のviewとして返すことも検証する。

`timestamp`は取得UTC、設備値の品質とSafety/watchdogは実時間のまま。
配送/認証/generationとreset履歴の永続化・初期化/復旧、厳密step fence、実Ingress検証、数値queryは未確定/未実装。
製品コードは今回変更せず、fixtureの認証済み/valid例を実環境の安全性の証拠としない。

## 検証と後続の受入条件

リポジトリrootから次を実行する（pySHACL 0.31.0、RDFLibはその依存）。

```bash
uv run --with pyshacl==0.31.0 python docs/design/ri-606/verify_contract.py
```

この検証はTTL構文/SHACL、35点のIDと属性のRDF↔JSON対応、hash、参照展開、run/telemetry整合、
不正例の拒否と、旧RI/指定Nexus形式・世代遷移・再送時の原文不変をローカルで確認する。
scenarioの期待値を検証関数から生成せず、fixtureに明記する。製品importや配布サービスのテストではない。

後続実装では以下を各ownerのPRに割り当て、横断成功の証跡を揃える。

| 境界 | 受入条件 |
|---|---|
| Builder → RI | 正式schemaとnamespace mapping、往復編集、重複入力・欠損native・dangling・越境・競合拒否。35/1/2の件数を別々に照合 |
| RI import → provisioning | 冪等import、clockだけの更新で新ETag、cache/push取りこぼし、削除/rollback、認可前304防止、他GW取得拒否 |
| 通常Point APIs/UI/control | 検索/一覧/チャート/制御でclock非混入。設定取得/exportでは権限付き保持。業務time telemetryゼロ |
| Nexus poll | chunk途中advance、ABA/reset、時計欠損、部分失敗、独立clock混在、同step再poll、再接続、restart apply失敗、COV未対応、revision/run切替 |
| Nexus → RI → BDE | metadataをNormalizer/再送/Ingress/最新KV/Parquet/opt-in Timescale/履歴query/集約/BDEまで保持。数値120.5等を文字列順で処理しない。run越境しない |
| 互換性 | ADRのold/new組合せ、旧RDF、clockなし通常BACnet、bootstrap promotion、full snapshot rollback |

未決事項とownerはADR末尾。正式JSON Schemaとontology releaseへの昇格は共通合意後に行う。
