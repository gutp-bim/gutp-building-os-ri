# 設備に関連付ける ClockSource と Gateway provisioning

- Status: **Proposed**（2026-10-24）。本書・fixtureは合意前の案であり、実装済み契約ではない。
- 関連: [RI #606](https://github.com/gutp-bim/gutp-building-os-ri/issues/606)、[Builder #44](https://github.com/smartbuilding-co-creation-organization/smartbuilding_datamodel_builder/issues/44)、[Nexus #201](https://github.com/gutp-bim/nexus-gateway/issues/201)
- 詳細・検証入口: [設計と契約fixture](../design/ri-606/README.md)

## Context

BOPTEST の time は実験インスタンスで共有する時計であり、設備の計測Pointではない。
設備Pointは35点（計測31、書込4）、IDは BPT-0002〜BPT-0036を維持する。
BPT-0001は廃止・再利用禁止。time/advance をPoint Listへ戻さない。
BACnet deviceは通信上の単位でありEquipmentと一対一とは限らない。

現行RIのPoint List DTO・ETag・diffはPointだけを扱う。clockだけの変更では既存ETagは変わらない。
Ingress attributesは文字列mapとしてdataへ転記されるが、sim数値軸による分析まで保証しない。

## Decision proposal

1. `bos:ClockSource`を`sbco:PointExt`と別クラスとし、Equipmentから
   `bos:usesClockSource`で参照する。`bos:`は既存の
   `http://buildingos.gutp.jp/ontology#`を用いる。追加語彙は本案独自の未承認拡張でありSBCO標準ではない。
2. 初期版はEquipment全点への適用、各Equipmentは最大1 clock、同じclockを複数設備で共有可能。
   点単位の参照・subset・overrideは拒否する。複数設備に属するPointも本契約では拒否し、
   優先順位で曖昧さを解決しない。独立clockは別IRIで明示する。
3. clock IRIは安定したopaqueな識別子とする。nativeアドレスやrunをIRIに埋め込まず、
   接続先変更でIRIを再採番しない。gateway所有権は不変、移管は新IRIで明示的に行う。
   clockの単位・native設定・適用設備の変更は静的revisionを更新する。
4. 取得設定は専用`bos:BacnetClockBinding`に分離する。既存SBCO native述語のdomain/rangeを
   ClockSourceへ拡張したと仮定しない。clock domainは初期版`simulation`、単位は秒を表す`"s"`、
   readOnly=true、取得方式pollのみ。native単位secondsから秒への恒等変換のみとし、その他は拒否する。
   connector IDはGW内の設定IDでありネットワーク接続情報・資格情報ではない。
5. RIが静的定義・ID・適用関係の正本。Builderは編集/IRI mapping/RDF/SHACLを所有し、
   Nexus Coreがsnapshotを同期してnative設定を導出する。connectorにはcanonical Point IDを渡さず、
   Normalizerで既存のID解決を維持する。discoveryは候補提示のみ、名前timeの自動採用はしない。
6. 既存`/gateways/{id}/pointlist`はwire形式・ETag・diffを維持する。
   新設候補`GET /gateways/{id}/provisioning/v1`でpoints・clockSources・clockBindingsを
   単一の検証済みsnapshotとして配布する。既存APIへの単純追加は旧GWが無視して成功と誤認するため採らない。
   関連APIを別々に取得する方式もrevision混在と複雑な再試行を避けるため初期案では採らない。
7. runId/step/現在sim時刻は静的RDFと配布snapshotに保存しない。
   Testbed実行管理者が生成する別run contextを、静的snapshot revisionとclock IRIへ束縛する。
   実験resetは新runId。run context配送経路・認証方式は未決であり、fixtureはメッセージ本体案のみ。
8. 通常検索・チャート・Point制御・points配列にはclockを出さない。
   gateway認証済み設定取得、権限付きtwin管理/RDF exportでは取得できる。
   クラス分離だけで除外が保証されたとはしない。二重型付けも拒否する。

## Alternatives

| 案 | 評価 |
|---|---|
| ClockSource独立リソース + Equipment参照 | 共有時計と設備点の区別が明確。新規import/配布/管理契約が必要。推奨 |
| native時計をPoint化し用途/公開区分で除外 | native表現を再利用可能だが全検索・制御・exportのfilter漏れで業務点に混入する。pointSpecificationは表示用なので判別に使えない |
| GW独自registry/名前timeの推定 | 正本が増え、設備適用範囲・revisionの整合が失われるため不採用 |

## Revision / mutation / authorization

- 新snapshotのrevisionはschemaVersion、gatewayId、points、clockSources、clockBindingsの内容hash。
  generatedAtとrevision自身は除外する。UTF-8、JSONキー再帰ordinal順、空白なし、配列は
  Point ID / clock IRI / equipment IRIのordinal順。初期契約の静的な数値は整数とし、非整数の既存Point属性は文字列で保持して浮動小数表現差を避ける。
  ETagは`"sha256:<hex>"`（JSON内は引用符をescape）。同内容再importは同revision。
- clockだけの変更でも新APIのETagを変更。既存Point ListのETagは従来どおり。
  cacheはgateway+schemaVersion+revisionで分離し、clock・binding変更も全API podのgenerationを無効化する。
  pushは再検証ヒントのみ。pushの欠落時もETag pollで収束する。
- 初期新APIはfull snapshotとIf-None-Matchの304のみ。`since`は400（未対応）。clock deltaは導入しない。
  旧APIのpoint diffを新snapshotに合成しない。将来diffを設けるならbase revision完全一致が必須。
- RI importは全候補graphを検証してatomic commit。dangling、clock競合、native不正、
  gateway越境、重複IDを拒否し、失敗時は既存snapshotを維持する。
  単独clock削除で参照が残れば拒否。同一transactionで参照を外して削除できる。
  全binding/clock削除は空配列を明示した新snapshot。clock無効化とrun停止を調整する。
- Nexusは全snapshotをstage/検証し、poll停止・drain後にNormalizerとconnectorへ一括適用する。
  BACnetはrestart前提としMQTT hot reloadを流用したと仮定しない。
  apply失敗は最後の整合snapshotを保持し、sim成功と報告しない。旧run contextを新revisionへ使い回さない。
- rollbackは既知snapshot全体を再適用し、新run contextを発行する。遅延した古いrun contextは
  generationの単調増加で拒否する。再送済み観測は取得時のrevision/run/timeを保持し、再付与しない。
- GETは既存と同じgateway identity binding（mTLS ingress由来の信頼ヘッダ、外部入力は除去）とadmin診断例外。
  clock/binding/対象Pointのgatewayはすべて一致必須。adminでも越境snapshotを生成しない。
  gateway IDが分かるだけでは他gatewayの取得権限にならない。管理writeは別途twin管理権限が必要。

## Compatibility and rollout

| producer / consumer | 結果 |
|---|---|
| 旧RI / 旧GW | 現行動作 |
| 新RI / 旧GW | 旧APIで現行動作。sim metadata対応とは判定しない |
| 旧RI / 新GW | 新API 404/非対応。clock要求なしなら明示legacy動作、clock必須なら未準備として停止 |
| 新RI / 新GW | schema/capability検証・一括apply・一致run context後にsim有効 |

Builderも旧CSV/RDFはclockなしで従来出力。新clockを旧形式へexportする場合は明示エラーとし黙って落とさない。
Builder入力は35行CSV+clock sidecar案を推奨し、kind混在行案より件数混同を避ける。UI形式はBuilder側合意待ち。
file/url/fallbackの既存優先順位を保持し、clockを使うfile bootstrapも同じsnapshot schema。
fallbackのAPI昇格は一方向、障害でfileへ戻さない。
導入順は共通語彙合意→Builder/RI→Nexus→Testbed/BDEの横断検証→opt-in。
rollbackはrun停止→Nexusのclock無効化→旧配布経路。35点・廃止IDは維持し、time点を復活させない。

## Owners / unresolved decisions

| 責務 | 合意・実装owner / 依存 |
|---|---|
| 正本・import・認可・配布・保存/query | RI #606 |
| namespace mapping・入力・RDF/SHACL・往復export | Builder #44 |
| 同期・native検証・poll整合性・attributes・再送 | Nexus #201 |
| run発行・唯一のadvance所有者・step fence | Testbed管理者（関連task番号の紐付け待ち） |
| 数値sim軸・runをまたがない取得/評価 | BDE管理者（関連task番号の紐付け待ち） |
| 共通ontologyの正式所有repoとrelease | RI/Builder/Nexus maintainerで確認。RIに新たな正本を作らない |

未決: ontology所有repo/語彙承認、run contextの配送・認証・失効とgeneration永続化、
Testbedのstep fence能力、COV対応、履歴/集約/BDEの数値時間軸schemaとAPI、
新API capability/適用ACKの最終wire契約、Testbed/BDE依存task番号。
fixture検証の成功はこれらの承認や横断実装完了を意味しない。
