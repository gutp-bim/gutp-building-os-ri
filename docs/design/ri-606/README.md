# RI #606: ClockSource設計・契約fixture案

本ディレクトリは設計レビュー専用。製品コード、他リポジトリ、稼働環境への投入は行わない。
[ADR案](../../adr/0009-equipment-clock-source.md)を入口とする。
新しいAPI・語彙・run contextは未実装/未承認。ここは共通ontologyの正本ではない。

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
このURLは候補であり現行サービスには存在しない。

## 静的snapshotとrun contextの境界

`fixtures/run-context.json`は別の実行管理メッセージ案。
`(gatewayId, clockId, provisioningRevision)`完全一致、runId非空、generation正整数・単調増加、
status=active、issuedAt/現在UTC/expiresAtの有効期間を検証してから利用する。
同generation同内容の再送は冪等、同generation異内容は拒否。状態をGW再起動後にも保持し、
保持できなければ新context取得までsim未準備にする。generationはclockごとに採番、rollbackでも戻さない。
reset/再initialize/実行切替は新runIdと新generation。static revisionが変わったら同runでも新contextを要求する。
issuedAt/失効/通信鮮度/Safety/watchdogはUTCまたは実時間の単調時計を使いsim秒を使わない。
clockの現在値・runId・generationは静的RDFに入れない。

初期案はpoll専用。時計を取得前後で読み、context/revision/generationが変わらず前後値が一致しても、
resetして同値へ戻るABAやchunk中の変更を完全に排除したとはいえない。
`bracket-only`ではquality=`bracketed`まで。厳密対応は唯一のadvance所有者による
「step固定→全chunk取得→完了ACK→次step」のfenceを別途合意・検証して`verified`とする。
時計が進まない同step複数pollは合法で、poll回数からstepIdを作らない。
clock欠損/前後不一致/partial failure/reset/context失効/非対応COVでは以前のsim時間を流用せず、
実UTCの設備観測にquality=`unavailable`と既知のclock/revisionだけ付け、sim_time_s/stepを省略する。
不明runIdも省略。厳密sim分析には使わず欠落を計測する。設備値自体の失敗は既存品質契約に従う。

## Telemetry属性案

`fixtures/telemetry.json`はprotobuf JSON表記ではなく、protoフィールド名で示した読解用frame例。
attributesは既存map<string,string>へそのまま格納可能。現行protoの変更は提案しない。

| 属性 | 制約 |
|---|---|
| sim_clock_id | 静的clock IRI。gateway/point適用範囲と照合 |
| sim_config_revision | 取得時の静的snapshot revision。再送時も保持 |
| sim_context_generation | run contextのgenerationを10進文字列で保持 |
| sim_run_id | 実行管理者生成のID。確定したcontextがある場合のみ |
| sim_time_s | 非負・有限・10進の数値文字列、秒。NaN/Infinity禁止。bracketed/verifiedの場合のみ |
| sim_step_id | 信頼できるstep fenceのIDがある場合のみ。fixtureには含めない |
| sim_time_quality | verified / bracketed / unavailable。未設定はlegacy（確定扱い不可） |

`timestamp`はGateway取得UTCのまま。Normalizer/Store-and-Forwardで属性を再生成しない。
Ingressは所属・revision/run関係と予約属性を検証する必要がある（現行は未実装）。過去revisionの再送は
保持した履歴snapshot/contextで照合し、保持外なら未確定として扱い現行contextへ付け替えない。
権限付きquery/BDE出力は元属性を保持するとともにsim_time_sを数値として扱う契約が必要。
分析キーは少なくとも(clockId, configRevision, runId)、stepを省略可能とし、run越境集約を禁止する。
既存UTC rollupをsim軸の集約結果と称さない。Oracle/KPI/正解値は属性へ混入させない。

## 検証と後続の受入条件

リポジトリrootから次を実行する（pySHACL 0.31.0、RDFLibはその依存）。

```bash
uv run --with pyshacl==0.31.0 python docs/design/ri-606/verify_contract.py
```

この検証はTTL構文/SHACL、35点のIDと属性のRDF↔JSON対応、hash、参照展開、run/telemetry整合、
不正例の拒否をローカルで確認する。製品importや配布サービスのテストではない。

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
