# Gateway向け静的ClockSource配布（RI #606）

RI側の静的設定の取込み・検証・配布を実装した。語彙とwire schemaは
[ADR-0009](../adr/0009-equipment-clock-source.md)のdraftであり、共通ontologyとしての承認は別途必要。
通常のPoint Listには時計を追加しない。設備35点・BPT-0002〜0036を維持する
[合成fixture](../design/ri-606/fixtures/model.ttl)と[JSON例](../design/ri-606/fixtures/provisioning.json)を参照。

## 取得API

```text
GET /gateways/{gatewayId}/provisioning/v1
If-None-Match: "sha256:<previous-content-hash>"
```

- 200: `schemaVersion: "clock-provisioning/1-draft"`、gatewayId、revision、generatedAt、
  points、clockSources、clockBindings。参照のない場合も空配列を返す。run contextは含めない。
- ETagはrevisionと同値。`Cache-Control: private, no-cache`。
  正しい認可と整合性検証の後に一致すれば304。弱いETag/list/`*`にも対応する。
- `since`指定は400。新APIはfull snapshotのみ。旧pointlistのdiffと合成しない。
- 不正なclock設定を検出した場合は409。詳細IRIはgateway応答へ出さず、管理者のimport previewで確認する。
- 認可は旧配布APIと同じgateway identity bindingとadmin JWT診断例外。未認可は403。
  mTLS ingressで検証済みの信頼ヘッダのみを用い、一般経路では同名ヘッダを除去する。
  `/api/v1`配下のaliasは設けない。インフラ変更・公開はこの実装では行っていない。

pointsとclock設定は1回のSPARQL SELECTで同じread transactionから取得する。
旧APIのPoint専用cache/revisionを再利用せず、各要求で読み直すためclockだけの変更・削除も
次のpollで検出でき、API replica間のcache無効化漏れが生じない。
初期版の代償は304にもlive queryが必要な点。大規模配置では計測後にcache最適化を検討する。

hash対象はschemaVersion、gatewayId、points、clockSources、clockBindings。
キーは再帰ordinal順、配列はPoint ID / clock IRI / equipment IRI順、UTF-8・空白なし。
nullプロパティは新APIの本文/hash双方で省略する。generatedAtとrevisionは除外する。
既存Point List APIのDTO・ETag・diff形式は変更していない。

## 取込みと適用

既存の管理API `POST /api/v1/admin/twin/import/preview` / `import/apply`を利用する。
追加の`clockIssueCount` / `clockIssues[] { resourceId, reason }`が不正clockの詳細を示す。
clock不正は`valid=false`、applyは409で拒否し、allowOrphansでも解除できない。
新schemaはEquipment全点への適用のみ。点override・複数設備所属・複数clock競合は拒否する。
同じclockを複数Equipmentで共有できるが、すべての対象Pointはclockと同じgatewayに属する必要がある。
`bos:ClockSource`をPointとして二重型付けすることも拒否する。

SBCO native述語のdomainを拡張せず、専用bos:BacnetClockBindingで取得元を記述する。
integerの範囲・必須項目・IRI/literalの区別・単位・read-only/pollを検証する。
clock対象Equipmentはsbco:idも必須とし、points[].device.dtIdと設備参照を必ず結び付けられるようにする。
実機objectの存在・読取権限・報告単位のprobeはNexus側の後続作業。
runId等をClockSource/native定義に付加するとunexpected_static_propertyとして拒否する。
clockなしの旧RDFには新しい所属制約を課さない。

適用は以下の手順で行う（startup seedも同じmaterializerを使用）。

1. 入力を呼出しごとに異なるnamed graphへloadし、既存twinのbaselineを確保する。
2. 入力を正規化したcandidateを作成する。appendはbaselineを併合し、replaceは入力だけとする。
3. candidate全体のclock整合性と既存clock IRIのgateway所有権不変を検証する。
4. baselineとlive twinが一致するときだけ、単一UPDATE transactionでdefault graphを置換する。
   検証中の並行更新は409で拒否し、previewから再試行する。同じclock構成の再importは同revisionとなる。
5. 一時graphをcleanupし、成功したreplaceのraw入力だけは従来の`urn:bos:twin-source`へ保持する。

clockの参照を残した定義削除は拒否する。削除・更新・rollbackは管理者が
整合したtwin全体をreplaceする（appendは従来どおり追加のみで、既存値の上書き操作ではない）。
最終commitは原子的だが、baseline/candidateは事前に作るためimport時の一時領域・処理量はtwin全体に比例する。
既存metadata編集等の別更新が入れば比較で検出する。直接OxiGraphへ書き込む運用はこの検証を通らない。

## 検証と後続範囲

`ClockProvisioningTest`は使い捨てOxiGraph 0.5.0で、35点fixture→import→snapshot→JSON hash、
旧Point Listとの一致、検索/Point取得への非混入、拒否時の既存revision保持、削除・rollback、
Gateway範囲、並行更新の保持を検証する。Controllerテストは認可前304防止とcache policyを検証する。

今回含めないものはrun contextの配送/認証/失効、数値sim時間query/集約、Telemetryのsim語彙検証、
Builder/Nexus/BDE/Testbedの製品変更、稼働環境への配布。新APIが返ることはGatewayへの適用成功を意味しない。
共通ontology所有repo・正式schema・capability/適用ACKは引き続き横断合意が必要。
