# テレメトリーのサイズ制限とHotの付加情報

RI #607。validated telemetryの `data` をHotでも保持し、raw履歴と同じJSON文字列を
最新値DTOへ渡す。新しいclock/run契約の検証やsim時刻の生成は行わない。

## 設定

ConnectorWorkerの環境変数／外部Configurationから読み取り、起動時に確定する。
変更後は、送信元となるすべての `all` / `ingest` ワーカーを同じ値で再起動する。

| 設定 | 既定 | 計測対象 |
|---|---:|---|
| `TELEMETRY_MAX_DATA_BYTES` | 1024 | validated wire上の1観測の `data` JSON値 |
| `TELEMETRY_MAX_RECORD_BYTES` | 16384 | validated wire上の1観測オブジェクト全体 |

- 両方とも正の10進整数。未設定は既定値。空文字・0・負数・非整数・整数範囲外は起動時エラー。
- Record上限はData上限以上でなければ起動時エラー。0による無制限指定はない。
- 1KiBはDataの既定値。行全体はID・UTC・メタデータ・文字列値の余裕として16KiBを用意する。
- 判定はUTF-8バイト数で、上限と同じサイズは許可する。プロパティ名、引用符、JSONエスケープ、
  オブジェクト内部の空白を含む。文字数やgRPC protobufサイズではない。
- Gatewayはtwinによる補完、UTC正規化、正本 `gatewayId` の追加後のJSONを計測する。
  `gatewayId` もData予算に含む。attributesからの同名値による上書きは従来どおり禁止する。
- Data欠落／nullはDataサイズ0。`{}` は2 bytes。ただし観測全体ではそのプロパティ表現も数える。
- envelopeの `telemetries` 配列、観測間の区切りや空白、KV/レスポンスの再エスケープは対象外。

例：Dataを2KiB、観測全体を32KiBへ変更する場合：

```dotenv
TELEMETRY_MAX_DATA_BYTES=2048
TELEMETRY_MAX_RECORD_BYTES=32768
```

## 適用位置と拒否動作

`NatsKvPublisher` と `NatsIngressTelemetryBus` のvalidated publish直前に同じ検証を適用する。
メッセージ全体を検証し終えるまでNATS publishとHot writeを開始しない。
Dataの切捨て、フィールド削除、値・時刻の再生成は行わない。

- Gatewayは1 frame→1観測のenvelope。超過フレームはpublishせず、StreamAckのAcceptedに含めない。
  `size_exceeded` を記録して次のフレームを処理する。既存ACKは受理数のみで、
  拒否されたPointや理由を個別配送する新しいproto/APIは追加しない。
- Connectorの複数観測envelopeは、1件でも超過すると全体を拒否する。
  例外は既存のworkerエラー処理へ返す。既存のat-most-once方針（ADR-0001）は変更しない。
- raw subjectは対象外。NATSへ直接publishする外部クライアントや、decoratorを通さない独自実装には
  このアプリ制限は適用されない。ブローカーの制限を置き換える機能ではない。
- 保存済みKV・Parquetを読む際には新しい上限を適用しない。上限を下げても既存データを消さない。

## 互換性

`ValidTelemetryData.Data` は既存のnullableなJSON文字列。新しい列、DTO、KVキー形式は追加しない。
Hotの欠落を修正し、JSONオブジェクトの表現を履歴と同じ方式で保存する。
Data欠落／null、通常のnumeric/string/boolと取得UTCは維持する。
Dataありの観測の後にDataなしの観測が来たら、行全体の置換によりDataもnullになる。
過去のDataを新しい値へ引き継がない。既存KVの欠落情報は次回受信まで復元されない。

新しい既定上限より大きい入力は、従来受け入れていても拒否される。導入前に通常入力の
実測とNATS/KV設定を確認し、必要なら上限を明示してからワーカーを切り替える。
上限以内のDataなし入力のJSON/値/UTCの扱いは変えない。

## KV・応答・負荷

KVは引き続き `telemetry-latest`、`History = 1`。この変更でバケットのMaxBytes、MaxValueSize、
TTLや既存設定を上書きしない。APIの権限判定も変更しない。

DataはJSON文字列なので、KV保存とAPI応答では再エスケープされる。観測が上限以内でも、
KVの1値上限やNATSの `max_payload` に必ず収まる保証にはならない。
特に上限を大きく設定すると、validated publishは成功してKVだけ失敗する場合がある。
NATSの既定メッセージ上限は1MiB。同梱設定は `max_payload` を上書きしていない。
実配備ではサーバー／アカウント／Stream／KVの実効上限を別途確認する。
[NATS設定資料](https://docs.nats.io/reference/config/)。

同梱ローカルNATSのJetStream file storeは全Stream共有の4GBで、KV専用の容量ではない。
History=1でもPoint数に応じた保存量、書込みごとの通信とファイルI/O、KV watch利用者の
受信・JSON解析コストは増える。publish前の検証も追加のJSON解析と文字列確保を伴う。

Hot同期は従来どおりbest effort。validated publish後のHot失敗は受信成功を取り消さず、
古いlatestが残り得る。Dataだけ削除して新しい値を保存するフォールバックは行わない。

最新値の単点 `query?latest=true`、`query/batch-latest` のLatestSample、raw `query/batch` は
DataをJSON文字列として返す。batch-latestの `data` は未設定・欠測時にnullとなる。
RI #607は内部Hot投影のData保持を修正し、その後のAPI拡張でbatch-latestにもDataを追加した。
500 Point等の件数制限は応答バイト数の上限ではない。
今回、全HTTP応答への新しいバイト上限やページングは追加しない。

## 観測

- `building_os.telemetry.size_rejections`：拒否envelope数。最初の超過で1増加。
  tagsは `source=connector|gateway-grpc` と `dimension=data|record`。
  単独でvalidatorを呼ぶ場合のsource既定は `unknown`。
- `building_os.telemetry.hot_write_failures`：Hot投影／put失敗数。tagはsourceのみ。
  呼出し元のキャンセルによる正常停止は数えない。
- Gatewayの既存 `building_os.ingress.messages` に `result=size_exceeded` を追加。
- サイズ例外／Gatewayの拒否ログには対象種別と実測・上限の数値だけを含める。
  Data本文を新しくログ・メトリクスへ載せない。

最新値を読む権限を持つ利用者へDataも返るため、送信側は機密値を属性へ入れないこと。
Dataを認証・認可やgateway所有権の根拠に昇格させない。

## 検証

TDDと環境障害を含む実行結果は [RI #607検証報告](../design/ri-607/README.md) を参照。
実KVの検証は、NATSイメージ取得の環境障害で未完了。コンポーネント結合と区別する。
