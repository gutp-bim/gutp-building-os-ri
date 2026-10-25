# RI #607: Data保持と設定可能なサイズ制限の検証

基準: RI `9083af2f851a38a442f90420e3cef913081c66a7`。
対象: Hot投影のData保持、validated publish前のサイズ制限、拒否・Hot失敗の可視化。
[仕様と設定](../../architecture/telemetry-size-limits.md)。

**状態: 実装・単体・HTTPコンポーネント結合は検証済み。実NATS KV結合は環境障害で未完了。**
稼働環境・他リポジトリは使用／変更せず、push・mergeも行わない。

## TDD

1テスト／1挙動ずつ失敗→最小実装→成功を確認した。

| サイクル | 先に確認した失敗 | 実装 |
|---|---|---|
| 1 | 時計属性付きのHot.Dataがnull | Dataを履歴と同じJSON文字列で保持 |
| 2 | Data超過バッチがpublishに到達 | publish前のenvelope検証 |
| 3 | Dataなしの巨大な文字列値を受理 | 観測全体の上限 |
| 4 | 設定可能なUTF-8境界API未実装によるビルド失敗 | immutableなサイズ設定と境界例外 |
| 5 | 0・負数・空・文字列・整数範囲外・大小矛盾の設定を許可 | 起動時の設定検証とDI |
| 6 | Gateway busでサイズ検証前にJetStreamへ到達 | 実busのpublish前検証 |
| 7 | Hot投影／put失敗のカウンターがない | ペイロードを含めない失敗カウンター |
| 8 | サイズ拒否のカウンターがない | source/dimensionによる拒否カウンター |
| 9 | Gatewayでサイズ拒否をpublish_failedと分類 | size_exceededと受理数・継続処理 |

既存テストに加え、Data欠落／null／空オブジェクト、Dataあり→なし、numeric/string/bool/UTC、
既定1024/1025 bytes、観測の境界、UTF-8/引用符/内部空白、設定変更、raw subject非適用を確認。
一部の境界・互換性テストは実装後の追加回帰検査であり、すべてをRED起点とは主張しない。

## 実行結果

ソースをbuildして実行。`--no-build` で古いassemblyを使う実行ではない。

| 検査 | 結果 |
|---|---|
| Shared.Test全体 | 1497成功、失敗／skip 0 |
| ApiServer.Test全体 | 694成功、失敗／skip 0 |
| TelemetryDataHttpContractTest | 3成功、失敗／skip 0 |
| TelemetryDataContractTest: 実NATS/KVの3ケース | fixture初期化で環境エラー3。テスト本文未実行 |

HTTP検査はテスト用Kestrel listenerをloopbackのランダムポートに起動する。
実際のTelemetryController、router、DTO、JWT署名・issuer/audience/lifetime検証、
AuthorizationContextMiddleware、DefaultAuthorizationServiceを使用する。
JWT署名鍵は実行時生成で、鍵とJWTは成果物へ保存しない。
未認証／不正JWTは401、権限なしは403、建物read権限付きは200を確認。

Hot adapter、twin metadata、hierarchy/group resolverはfixture。
Hot投影そのものは製品のValidatedTelemetryHotStoreを直接実行する。
履歴は製品serializerで一時ローカルファイルへParquetを書き、閉じた後に製品readerで読む。
実MinIO/S3・NATS/KV・Keycloak発行/OIDC discovery・mTLS・gRPC listener・実Gatewayや設備は
この成功したコンポーネント検査には含まない。製品Startup全体の配備検査でもない。

35点と500点はテストが生成するPoint集合。BuilderのRDFを再importする検査ではない。
35点は全点でHTTP latest/rawのData・値・取得UTCを照合。
500点は全点でHTTP latestを照合し、raw batchとcompact batch-latestを検査する。
compact batch-latestにDataが増えていないことも確認する。

## 計測値

同梱 `sizes-component-35.json` / `sizes-component-500.json` は実行コードの出力を保存したもの。
`actualKv=false` を明記しており、KV実機の測定結果ではない。
Hot行のサイズはNatsKvLatestStoreと同じ既定JSON serializerで算出する。
Dataはこの入力で203 bytes、Hot行は最大545 bytes。

| 項目 (bytes) | 35点 | 500点 |
|---|---:|---:|
| validated envelope | 13632 | 194517 |
| HTTP raw batch応答 | 16906 | 241501 |
| HTTP compact batch-latest応答 | 3921 | 56001 |
| HTTP単点latest応答の合計 | 15050 | 215000 |

clock/run属性は保存対象としてそのまま運ぶ。今回、新しい時刻契約の妥当性判定は追加しない。

## 実KVの環境障害と残作業

Docker Serverへの接続確認は成功した。しかしfixtureが要求する `nats:2.10-alpine` が
ローカルキャッシュになく、Docker Registryへの接続がDocker側のネットワーク拒否で失敗した。
コンテナ起動前の障害であり、コードのREDや成功には数えない。
既存のNATS fixtureやセキュリティ設定を変更して回避せず、稼働中のbrokerも使用していない。

環境復旧後、同梱の3ケースを再実行する必要がある。

- real bus→JetStreamに永続化したbytes→実KV→認証HTTP latest/raw（35点・500点）。
- 上限を600000/700000 bytesへ上げるとwireはNATS既定上限以内でも、
  KVでのData再エスケープが上限を超え、最新値は以前のまま残るケース。

このwire/KVサイズ差そのものはDocker不要のコンポーネント検査でも確認する。
実KVでの失敗・ログ・旧latest保持の結合証拠はまだない。
よって #607全受入完了や実経路E2E完了は主張しない。
Testbed側の既存13 checksの固定SHA再実行も今回の成功証拠には含まない。
新しい製品コミットを指定した再実行が別途必要になる。

## 再実行

```sh
dotnet test DotNet/BuildingOS.Shared.Test
dotnet test DotNet/BuildingOS.ApiServer.Test
dotnet test DotNet/BuildingOS.IntegrationTest --filter FullyQualifiedName~TelemetryDataHttpContractTest
dotnet test DotNet/BuildingOS.IntegrationTest --filter FullyQualifiedName~TelemetryDataContractTest
```

テストの計測出力: `DotNet/BuildingOS.IntegrationTest/bin/Debug/net8.0/TelemetryContractArtifacts/`。
実行ログ・TRXはGit管理外の `out/ri607/`。fixture取得エラーも保持する。

## レビュー

製品差分、DI、全件検証前に副作用がないこと、null互換性、キャンセル、
メトリクスのcardinality、API認可、設定例、テストの実経路／fixture境界をレビュー済み。
レビューでbatch-latestがDataを返すという当初の想定を訂正し、コンパクトDTOを維持した。
また、仕様のソース既定値とfixtureの権限文字列を既存の契約に合わせ、再実行で確認した。
既定上限による大きい既存入力の拒否と、上限を上げた際のwire/KVサイズ差は仕様に明記。
実KV未検証は未決事項として残す。KV設定や認証設定を勝手に拡大しない。
