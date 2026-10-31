# Builder #44 → RI #606 結合検証（2026-10-25）

結果: **成功**。指定Builder生成RDFをRI基準版の実import/materializerへ通し、
`GW-BOPTEST-001`のprovisioning/v1相当のJSONをRIの実装から生成した。

## 基準と入力

- RI製品ソース基準: `9b643391d7273de4d92166d2474de1e5e0ea9f83`。今回の変更は結合テスト・入力fixture・検証記録のみ。
- Builder生成元として指定された基準: `f2f313e044b85f7ef127ef95351afef81afdcbc9`。local git objectの存在を確認。
  Builder自体は今回再build/再生成せず、依頼された生成済みartifactを入力として用いた。
- 入力元の論理位置: `building-os-e2e/artifacts/builder-44-f2f313e/`。
  このディレクトリの`inputs/`はmodel.ttl / clocks.jsonをbyte単位で保存した。
  CSVだけはGitの末尾空白チェックを回避せず通すため、追跡用の改行をCRLFからLFへ正規化する。
  テストは元のCRLFへ復元し、元ファイルのhashと完全一致を確認したbyte列をCSV parserへ渡す。データ欄は変更していない。
  個人用パス・入力側の実行ログは追跡しない。

| 入力 | SHA-256 |
|---|---|
| [model.ttl](inputs/model.ttl) | `86098affbeb550b0e51a1e8268370ede94120ad45eb32c999a19f9c0eb117c87` |
| [pointlist-v2.csv](inputs/pointlist-v2.csv) | `0813b8d3f60a24d8ba2459c4dd169225f9d50d3810bcf17e31ce9febabcd338e` |
| [clocks.json](inputs/clocks.json) | `356c5213574c234c855269a5c3128ad4eaec4793c813e578c38e4b34b73d6816` |

このSHAとの完全一致をテストの最初で検証する（CSVはCRLF復元後）。
追跡用LF版CSVのSHA-256は`9e4ae5bcf32376fc2e9808a62419126f9ada00782d5cddc89acbb91a63be2310`。生成元コミットとの関連は提供された来歴に基づき、
今回Builderを再実行して生成再現性まで検証したものではない。

## 検証経路・隔離

既存`ClockProvisioningTest`に`BuilderGeneratedRdf_ImportsAndExportsMatching35PointSnapshot`を追加した。
同じ`OxiGraphFixture`（Testcontainers）で、新規の`ghcr.io/oxigraph/oxigraph:0.5.0`を起動し、
ランダムhost port・使い捨てstoreで実行後に削除した。既存コンテナ・Compose・稼働中stackは使っていない。
Docker Serverは`29.1.3`で、起動・readiness・終了/削除をtest logで確認した。

```text
Builder model.ttl
 → OxiGraphTwinAdminService.PreviewImportAsync(Replace)
 → OxiGraphTwinAdminService.ApplyImportAsync(Replace)
 → OxiGraphIngestMaterializer（候補graph検証・原子的commit）
 → OxiGraphGatewayProvisioningSource.ReadAsync
 → GatewayConfigurationController.Get（actionの直接呼出し）
 → GatewayConfigurationResponse.From / 実JsonResultのSerializerSettings
 → results/provisioning.json
```

**Gateway APIへのHTTP要求は未実施。** ASP.NET host・route/middleware・mTLS・JWT・プロキシ経路を
通していない。controller action・実source/DTO・製品のJSON serializer設定までの検証である。
OxiGraphへのSPARQL/graph-store HTTPは隔離コンテナに対して実行した。
Nexus/Gatewayへの接続、BACnet実機通信、Telemetry送信、制御、runtime run contextは検証対象外。

## 結果

| 確認事項 | 実測・結果 |
|---|---|
| 設備Point | 35点、BPT-0002〜BPT-0036。CSVと全ID一致。31 read-only / 4 writableも一致 |
| ClockSource / 設備参照 | 1件 / 1件。設備DEV-FCU-1のIRI・clock IRI・native binding IRIをsidecarと照合 |
| BACnet / protocol | 全35点のdevice ID・object type・instance・local IDをCSVと照合。RDF内の明示bos:protocolと配布DTOのprotocolも一致 |
| clock native設定 | sidecarのgateway/connector/device/object/property/unit、clock domain/read-only/pollを保持 |
| Building / Point参照 | Building ID=`bldg-boptest`。35点のsbco:buildingとCSV building/building_idが一致 |
| Ingress用metadata source | 35点すべてHasBuildingPath=true、Buildingが同じID。実Ingressへのフレーム投入は未実施 |
| 通常Point一覧 / 検索 / 旧Point List | すべて35点・CSVと同一ID。clock IRI・廃止BPT-0001・clock用native addressが混入しない |
| 再import | preview valid、clock/orphan/id collision/release issueなし。全snapshot revision・旧Point List ETag・default graph 912 triplesが不変 |
| ソース再build | --no-incrementalで再build成功、0 error（compiler/analyzer警告391件、抑止なし） |
| 実行 | ClockProvisioningTest **19/19 passed**。追加結合テストと既存18ケースが成功 |

CSVの空unit（BPT-0018/0019/0021/0035）はBuilderがRDFから省略し、RIも未定義として返す。
最初のテストは空文字との直接比較で失敗したため、期待値だけをnull/属性省略へ修正し、再build・全19ケース再実行した。
非空unitはCSVと完全一致を要求している。製品コード・入力は補正していない。

previewのraw RDFは948 triples、正規化default graphは912 triples。
既存materializerの規則ではrec:description（入力35件）とrec:Siteの型（1件）はdefault graphへ移さず、
入力原文はurn:bos:twin-sourceに保持する。要求されたPoint/native/protocol/building/clock項目は上表のとおり保持を検証した。
全述語の完全なRDF round-tripを保証する結果ではない。

## 成果物

- [実生成provisioning.json](results/provisioning.json): 手書き・Pythonによる組立てをせず、実controller/source/DTOから生成。
- [checks.json](results/checks.json): 入力hash、チェック結果、preview/reimport preview、検証境界。
- [run.json](results/run.json): 基準SHA、build/test commandと成功件数、HTTP未検証の記録。
- 生のbuild/test log・TRX: リポジトリrootの`out/ri-606-builder-f2f313e/`（Git管理外、個人用パスを含むため未commit）。
- 実行時のJSON出力先: integration testのbuild output配下`BuilderClockArtifacts/`。
  すべてのassertionが通った後だけ出力し、前回結果は次回テスト本体の開始時に除去する。
  成果物の存在だけで成功と判定せず、build/test exit codeとTRXの失敗ゼロも照合する。

snapshot revision: `"sha256:f38674f3223990430f78f33905c34c5e1b44ea0e79cb7d641af6a5b8a17f6a16"`。
保存したprovisioning.jsonのSHA-256: `b725e01075fbcfcf672e0a6f7021491d96ee584ce49de9bd8de12c0338d8e41e`。
JSONのgeneratedAtは実生成時UTCのまま保持し、report向けに書き換えていない。

## 再実行

リポジトリrootから（Docker必須、稼働stackは不要）:

```bash
mkdir -p out/ri-606-builder-f2f313e
dotnet build DotNet/BuildingOS.IntegrationTest --no-incremental --verbosity minimal
dotnet test DotNet/BuildingOS.IntegrationTest --no-build --no-restore \
  --filter 'FullyQualifiedName~ClockProvisioningTest' \
  --logger 'console;verbosity=normal' \
  --logger 'trx;LogFileName=clock-provisioning.trx' \
  --results-directory out/ri-606-builder-f2f313e
```

Dockerが使えなければテストは失敗させ、fixtureの静的チェックだけで成功扱いしない。
今回、他repo・製品ソース・稼働環境は変更していない。push/mergeは行わない。
