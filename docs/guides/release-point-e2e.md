# Release point の連携検証（#600）

`fixtures/e2e/twin-release.ttl` はローカルテスト専用の twin。
gateway `GW` に BACnet `analogOutput,37` の `TARGET` と、その解除点 `RELEASE` を定義する。
実設備につながる gateway には配布しない。

## RI の統合テスト

```bash
cd DotNet
dotnet test BuildingOS.IntegrationTest --filter FullyQualifiedName~ReleasePointTest
```

実 OxiGraph で replace/append の検証、`releaseOf` の pointId 変換、protocol の継承、
対象不在・gateway 不一致・不正スキーマの拒否を確認する。

## nexus-gateway との E2E

nexus-gateway の release 対応（`99e11a9`、#181）以降を使う。
専用のローカル PostgreSQL・NATS・OxiGraph・Keycloak を用意し、このブランチの
ApiServer と GatewayBridge を同じ NATS に接続する。ApiServer は認証を有効にし、
`OXIGRAPH_SEED_TTL_PATH` にこの fixture、`GatewayConnectionTypes__Map__GW=bacnet-sim`
を指定する。既存の twin を上書きしないよう、OxiGraph と PostgreSQL は検証専用にする。

Keycloak の管理者アクセストークンと、その JWT の `sub` をそれぞれ
`E2E_BOS_TOKEN`、`E2E_BOS_ACTOR_SUB` に設定する。トークンをログやファイルに記録しない。
Python 環境には gateway の `connector/bacnet` とその依存関係をインストールする。

nexus-gateway のルートから実行（URL と Python パスは自分のローカル検証環境に合わせる）:

```bash
E2E_BOS_RELEASE=1 \
E2E_BOS_API_URL=http://localhost:15500 \
E2E_BOS_EGRESS_ADDR=localhost:15502 \
E2E_GATEWAY_ID=GW \
E2E_RELEASE_POINT_ID=RELEASE \
E2E_WRITABLE_POINT_ID=TARGET \
E2E_BACNET_PYTHON=python3 \
go test ./integration -run '^TestE2E_BosReleaseDispatch$' -v -count=1 -timeout 90s
```

このテストは実 RI の provisioning 応答を gateway の mapper に渡し、REST 制御から
GatewayBridge、gateway dispatcher、Python の production write handler/client まで通す。
BACnet の Application I/O 境界はローカルの BACpypes commandable object に置き換えるため、
BACnet UDP 通信や実設備との疎通は対象外。

確認内容:

- 通常の書込で値が `22` になり、release 点への `1` で既定値 `18` に戻る。
- 両操作の `controlId`・pointId・実行者 `sub`・成功結果が RI の監査に残る。
- release 点への `0` は `400`、未認証の操作は `401` または `403` になり、追加の書込を行わない。

2026-10-24 に RI `a10ca93` と nexus-gateway `99e11a9` の組合せで成功を確認した。

同日のブランチ検証結果:

| 検証 | 結果 |
|---|---|
| .NET 単体テスト | 2,189 件成功 |
| RI 機能統合テスト（追加の release テスト 5 件を含む） | 173 件成功 |
| Twin Admin のフロントエンドテスト | 21 件成功、型チェック成功 |
| 1 万点の point list | 初回約 0.77 秒、5 秒の基準を満たす |
| 10 万点の point list（計測のみ） | 初回約 75 秒 |
| 5 万点・20 gateway 同時取得（計測のみ） | p95 約 50 秒 |

25 万点の任意テストは未実行。大規模測定は性能保証を意味しない。
統合テストの OxiGraph は配布スタックと同じ `0.5.0` に固定した。
固定前の `latest`（検証時 `0.5.13`）では大規模テストが 100 秒でタイムアウトした。
