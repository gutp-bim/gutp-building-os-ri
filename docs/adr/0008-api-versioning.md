# REST API のバージョニング: `/api/v1` パス + 旧パスの互換エイリアス

- **Status**: Accepted（2026-09-29、maintainer 承認済み）
- **関連**: #507（本 ADR）、#504（提案 B = `MyResources` の元 ID 返却は本方針に沿った破壊的変更として扱う）、
  #509、#224（gateway の point list ポーリング）、ADR-0003（GatewayBridge）

## Context

REST API のパスにはバージョンが無く、接頭辞も 3 系統が混在していた。

| 系統 | 例 |
|---|---|
| 接頭辞なし | `/buildings` `/floors` `/spaces` `/devices` `/points` `/telemetries` `/resources` `/gateways` `/point-details` `/device-details` |
| `/api/…` | `/api/Groups` `/api/Users` `/api/MyResources` `/api/telemetry/health` `/api/system/…` |
| `/api/admin/…` | `/api/admin/twin` `/api/admin/gateways` `/api/admin/audit` … |

互換性に関わる変更はこれまでにも入っている（per-tier テレメトリ API の `[Obsolete]` 化、`valueText` / `valueBool`
の削除 #344/#359 など）。外部アプリケーション（Tenant Portal、BDE）と gateway（nexus-gateway の
`GET /gateways/{id}/pointlist` ポーリング）は、どの API をいつまで互換のまま使えるかを知る手段がなかった。
**すべての API は将来バージョンが変わりうる**ので、特定の API だけを「安定」と宣言するのではなく、API 全体を
版付きにする。

## Decision

### 1. 全 REST API を `/api/v1/…` に置く

- すべてのコントローラを `ApiRoutes.V1`（`api/v1`）の下にマウントする。旧 `/api/…` は `/api/v1/…`、接頭辞
  なしは `/api/v1/{root}/…` になる（例: `/buildings` → `/api/v1/buildings`、`/api/Groups` → `/api/v1/Groups`、
  `/api/admin/twin` → `/api/v1/admin/twin`）。
- OpenAPI（Swagger）には `/api/v1` のパスだけが載る。
- 対象外: `/health`（プローブ）、gRPC / gRPC-web（`/{package}.{Service}/{Method}`、proto 側で互換を管理）、
  `/swagger`・`/api-docs`（ドキュメント）。
- 版の選び方は**パス**とする。独自ヘッダ（`Api-Version`）やクエリも検討したが、(a) URL だけで版が分かり
  ログ・curl・ドキュメントで追いやすい、(b) Ingress がパスで振り分けている（Helm の Traefik は
  `PathPrefix(/api)` を API server へ送る — `/api/v1` への統一でこの規則に全 API が収まる）、(c) 生成クライアント
  （aspida）に全リクエスト共通ヘッダを差し込む仕組みが要らない、ことからパスを採った。

### 2. 旧パスは互換エイリアスとして残す（既存クライアントは無変更で動く）

- `LegacyApiPathRewriter`（ルーティングの前段のミドルウェア）が、旧パスを**プロセス内で** `/api/v1/…` に
  書き換える。コントローラの定義は 1 つのままで、二重定義や OpenAPI の重複は生じない。
- 書き換えた応答には次を付ける。
  - `Deprecation: @1790640000`（RFC 9745、非推奨化した日 = 2026-09-29）
  - `Link: </api/v1/…>; rel="successor-version"`
- 書き換えの対象は `/api/…`（`/api/v{数字}/…` を除く）と、上表の接頭辞なしの先頭セグメントに限る。
  大文字小文字は区別しない（ルーティングと同じ）。
- `Sunset`（削除日）はまだ付けない。削除日は §4 の条件を満たしてから決め、決めた時点で `Sunset` を付ける。

### 3. 破壊的変更のポリシー

- **破壊的変更は新しい版（`/api/v2`）でのみ行う。** `v1` の中では次をしない:
  - 応答フィールドの削除・改名・型の変更、`null` になりうるフィールドを増やすこと
  - リクエストの必須項目の追加、受け付ける値の範囲を狭めること
  - ステータスコード・エラー形式の意味の変更、既定値の変更で結果が変わること
  - パスやクエリパラメータの削除・改名
- **非破壊（`v1` の中で行ってよい）**: 任意項目・任意パラメータの追加、応答フィールドの追加、応答ヘッダの追加、
  新しいエンドポイントの追加。クライアントは未知のフィールドを無視すること。
- 新しい版を出したら、旧版は **最低 6 か月** 並行提供する。非推奨にした版には `Deprecation` を、削除日を
  決めたら `Sunset` を付け、CHANGELOG の `Deprecated` / `Removed` に記載する。
- CHANGELOG では破壊的変更を `**BREAKING:**` で明示する（既存の運用を継続）。
- OpenAPI の差分を `oasdiff` で検査し、`v1` への破壊的変更を PR の段階で検出する（#507 の後続 PR で導入）。

### 4. 旧パス（版なし）の削除条件

次をすべて満たしてから削除日を決め、`Sunset` を付けて最低 6 か月後に削除する。

1. web-client が `/api/v1` に移行済み（#507 の後続 PR）
2. リポジトリ内のツール・ドキュメント・テストが `/api/v1` を使っている
3. gateway（nexus-gateway）の point list ポーリングが `/api/v1/gateways/{id}/pointlist` に移行済み
4. 旧パスへのアクセスがログ／メトリクスで一定期間ゼロ（既知の外部アプリへの告知を含む）

## Consequences

- 既存クライアントは変更なしで動き続ける（旧パスは同じコントローラに書き換えられ、応答は同一）。
- OpenAPI のパスがすべて `/api/v1/…` に変わるので、生成クライアント（aspida / Zodios）は再生成すると新パスを
  使う。web-client は後続 PR で再生成と移行を行う（それまでは旧パスで動く）。
- `CreatedAtAction` などサーバが組み立てる URL は `/api/v1/…` になる。
- web-client（Next.js）自身も `/api/…` のルート（例: `/api/health`）を持つ。同じホスト名で API server と
  web-client を配置する場合、`/api` の振り分けは API server 側に `/api/v1` を送るよう Ingress で確認すること。
- 版の番号は `ApiRoutes` の定数 1 か所にあり、`v2` を出すときはコントローラ単位で `v2` のルートを追加する。
