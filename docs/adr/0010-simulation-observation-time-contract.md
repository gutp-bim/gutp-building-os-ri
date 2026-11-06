# Simulation観測の取得品質とrun contextを分離する

- Status: **Proposed**。RI内の契約更新方針は2026-10-25に利用者合意済み。Nexusとの横断承認・製品実装完了を意味しない。
- 関連: [ADR-0009](0009-equipment-clock-source.md)、RI #606、Nexus #201。
- Nexus参照コミット: `bc1a9e5137fec041df250e4f927299eaeb5d9a0f`。
  `docs/clock-prototype.md`、`connector/bacnet/bacnet_connector/{connector.py,event.py}`、
  `connector/bacnet/tests/test_clock.py`、`internal/provisioning/clock.go`を固定版で読んだ。
  GitHubからrefを取得できなかったためローカルgit objectで確認した。他repoは変更していない。
- この変更は契約文書・fixture・offline検証だけ。RI Ingressの本番検証処理、認証、run registry、数値queryは追加しない。

## Context

Nexus prototypeはpoll全体と各RPM chunk境界で時計と単位を検査する。`sim_clock_revision`は
clock単体のhashではなく、RIが配布した静的snapshot全体のrevisionである。
`sim_time_quality`はstable_unfenced / changed / missing / resetで、run/stepは生成しない。
resetは時計後退を検知するとconnectorプロセス中ラッチされ、restartでその履歴を失う。

以前のRI fixtureはrun contextを必須とし、bracketed / unavailableを使っていた。
これでは「時刻が安定して読めた」と「有効なrunを識別できる」を同じ判定にしてしまう。
本ADRはADR-0009と旧fixtureの実行時時刻部分を置き換える。静的RDF・設備35点・配布snapshot契約は維持する。

## Decision

### 1. 設定revisionはrunやclockの世代と区別する

新producerの属性名は`sim_config_revision`。値は**取得時に適用した静的provisioning snapshot全体**のrevision。
clock ID、run ID、generationと異なる役割を持つ。clock単体のhashを新設しない。
既知のNexus prototypeからの`sim_clock_revision`はreaderでaliasとして解釈できる。
両方があって同値なら同じ設定を指すが、異値なら不正metadataとして拒否する。
新producerはcanonical名だけを出す。旧イベントの原文は保持し、再送時の改名・現在revisionへの付替えは行わない。

### 2. 取得品質とrun有効性は直交させる

| sim_time_quality（新producer） | 意味 | sim_time_s |
|---|---|---|
| stable_unfenced | poll前・各chunk後の必要なclock/単位検査が成功し、全値が一致。ただしABAや厳密なstep対応は保証しない | 必須 |
| changed | 有効な時計値が取得中に変化。通常のadvanceでも起こる | 禁止 |
| missing | clock/単位検査の欠損・失敗・不正値等で対応を確定できない | 禁止 |
| reset | 後退検知、または解除されていないresetラッチで対応を信用できない | 禁止 |

Nexusと同じ優先度reset > missing > changed > stable_unfencedとする。
resetは当該pollでの後退発生だけを表すものではなく、実験resetを断定するものでもない。
時計の一時欠損後に同値へ復旧することは許容するが、欠損中に以前の時計値を再利用しない。
成功した設備値は部分失敗で捨てず、その設備値の品質と時計品質を別々に扱う。

| sim_run_status（新producer） | 意味 | sim_run_id / sim_context_generation |
|---|---|---|
| absent | contextなし。clockが安定していてもrunを推定しない | 禁止 |
| valid | 認証、発行権限、scope、revision、有効期間、取消状態、commit済みgenerationを検証できた | 必須 |
| invalid | 信頼できるcontextに期限切れ、不一致、取消、取得中切替、reset等の既知の不適合がある | 禁止 |
| unverified | context候補はあるが発行元・権限・世代履歴・commitを検証できない | 禁止 |

invalid/unverifiedは`sim_run_reason`も必須。他の状態ではreasonを付けない。
validは制御面のcontext検証結果であり、物理的run連続性やstep fenceの証明ではない。
送信されたvalidという文字列、context本文のauthenticated等を信用根拠にしない。
将来のNexus Core等のrun対応componentとRI側評価者が信頼できる状態から判定する必要がある。
現行Nexus指定版はrun非対応なので、既知の互換readerではabsentと解釈する。

### 3. contextなしの時計観測も保存可能とする

stable_unfenced + absentは正常な**診断観測**。sim_time_sを付与してよいが、run ID・generation・stepは作らない。
invalid/unverifiedでも、取得自体がstable_unfencedならsim秒は診断用として残せる。
反対に、changed/missing + validも合法で、run IDを保持してよいがsim秒は出さない。

| 取得品質 / run状態 | 用途 |
|---|---|
| stable_unfenced + valid | run内の探索的な数値sim時間解析候補。厳密step評価は不可 |
| stable_unfenced + absent/invalid/unverified | UTC軸の閲覧とsim秒の注記。run別sim時間集約・時刻joinから除外 |
| changed/missing + valid | run識別とUTC観測は利用可。sim時間joinは不可 |
| reset（contextあり） | 現runを無効化。run別sim時間解析から除外 |

unknown runという共有runへ集約しない。キーは少なくとも(gatewayId, clockId, configRevision, runId)。
数値解析候補でも別runと同時刻だからという理由でjoinしない。厳密stepは全状態で未対応とし、
sim_step_idやquality=verifiedを新producerは出さない。step固定・全点取得・完了ACKのfenceは別契約である。
UTC取得timestamp、設備値の品質、通信鮮度、Safety期限/watchdogは変更しない。
Oracle/KPI/正解データはmetadataへ混入させない。

### 4. run-context/2-draftを用いる

`fixtures/run-context.json`のschemaVersionを`clock-run-context/2-draft`とする。
gatewayId、clockId、provisioningRevision、runId、generation、status、issuedAt、validFrom、expiresAtを持つ。
statusはactive / revoked。issuedAt <= validFrom < expiresAt。時刻はUTC RFC3339。
generationはJSON整数1〜9007199254740991とし、boolやfloatを整数として受け入れない。
`consistencyMode`は取得方式と混同するためcontextから除去する。

有効性は点のtimestampだけでなく**poll開始から終了まで**の区間で判定する。
contextは開始以前にcommit・適用済みで、validFrom <= 開始、終了 < expiresAtでなければならない。
commitだけでは適用済みと判定しない。offline fixtureはcommittedAtとappliedAtを別々の受信者証拠として持つ。
取得中にcontext/revision/generationが切り替わったpollをvalidとしない。取得後に到着したcontextを遡って付けない。

認証できない場合はunverified、認証後にscope/revision/期間/取消/取得中切替を検査する。
次に復旧済みの世代履歴とcommit・適用を照合し、最後にresetラッチ・永続化されたrun無効化状態を確認する。
複数の問題が同時にある場合のreason優先度はoffline verifierのこの順序で固定する。

### 5. generationとresetの状態遷移

- generationは(gatewayId, clockId)ごとに単調増加。発行権限を持つ主体を一つの権限domainとして管理する。
- 同世代・同じ本文は冪等。同世代・異なる本文と過去世代は拒否。JSONのキー順の違いは本文差としない。
- 上位世代の受信だけでvalidにしない。検証・durable commit・適用境界を経た後に有効化する。
- reset検知時は対象runを無効化する。同じrunへの上位generationの更新やconnector再起動で解除しない。
  復帰は新run ID・上位generation・commit完了後に新しい取得履歴でpollする。旧runの無効化記録は保持する。
- 履歴が復旧できなければunverified。最初に届いた世代を無条件にwatermarkとしたり、generation=1へ戻したりしない。
- rollbackで静的revisionが以前の値へ戻ってもgenerationは戻さず、新しいcontextを要求する。
- 再送・保存済みイベントは取得時のsnapshot/context/検証状態で評価し、現在のrunや現在時刻で再付与・失効させない。
  保存履歴を確認できなければ未検証として扱い、推測でvalidに上げない。

### 6. 旧形式は診断用として互換解釈する

compatibility profileは既知のproducer版・保存時の来歴からreaderが選ぶ。イベント自身の自己申告では選ばない。

| 来歴 | 解釈 |
|---|---|
| Nexus bc1a9e5 | revision aliasを読む。4品質を保持。run状態はabsent。元イベントを書き換えない |
| RI 6e0fefeのbracketed | 前後一致の旧証拠だけ。全chunk検査を推定してstable_unfencedへ格上げしない |
| RIのunavailable | 原因不明の旧品質として保持。missing/changed/resetへ推定変換しない |
| RIのverified文字列 | fence証拠として扱わない。厳密step解析へ入れない |
| 旧RIのrun ID/generation | 原文を診断用に保持するが、未検証のrun主張として解釈。v2のvalidへ自動昇格しない |

新形式に不正なmetadataがあれば契約検証は失敗する。設備値そのものを必ず破棄する仕様ではない。
実Ingressでの隔離・保存・metricsの扱いは後続実装で決める。未知の形式を既知profileへ推測して通さない。

## Fixtures / verification

[fixtureと検証入口](../design/ri-606/README.md)を参照。
`time_contract.py`はofflineな契約oracleであり、実Ingress・認証・配送・永続化を実装するものではない。
`acquisition-evidence.json`は取得境界検査のテスト用証拠、`receiver-evidence.json`は受信者の信頼/commit履歴の
注入用fixtureである。どちらもtelemetry属性やrun context本文へ混入させない。
後者にあるauthenticated=true等はテスト入力であって認証方式ではない。

## 未決・未実装とowner

| 項目 | 後続で決めること | owner |
|---|---|---|
| 配送 | push/poll/API、開始前配送、ACK、切替境界、取消の到達保証、切断時の扱い | Testbed / Nexus / RI |
| 認証・権限 | 発行主体、署名/認証方式、gateway/clock scopeごとの発行権限、発行主体交代 | Testbed / RI / Nexus |
| 永続化 | generation watermark、本文一致情報、reset無効化run、初期化/復旧、atomic commitと有効化 | Nexus / RI |
| 厳密step | 唯一のadvance所有者、全chunk完了、fence証拠、step ID | Testbed / Nexus |
| 実Ingressと解析 | 過去revision/state保持、metadata検証失敗の隔離、数値sim時間query・集約・BDE | RI / BDE |

これらの方式・配置先・保証は本ADRで確定したとはしない。fixtureでvalidが通っても、現在の製品が
run contextを安全に配送・認証・復旧できる証拠にはならない。他repo・稼働環境への変更は行わない。
