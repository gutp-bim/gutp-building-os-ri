# クライアントアプリケーション開発ガイド

Building OS の REST API を外部アプリケーションから利用する手順を説明します。
認証フロー、主要 API エンドポイント、テレメトリ読み取り、リソース検索、デバイス制御を扱います。

API 仕様の完全なリファレンスは [`docs/schema/swagger.yaml`](schema/swagger.yaml)（または
起動中の API Server の `http://localhost:5000/swagger`）を参照してください。

---

## 1. 認証

### ローカル開発：DISABLE_AUTH=true

最も手軽な方法は API Server を認証なしで起動することです。

```bash
DISABLE_AUTH=true dotnet run --launch-profile WithLocal
# → すべてのリクエストが管理者として扱われる
```

この場合、`Authorization` ヘッダーは不要です。

### Bearer トークン（Keycloak OIDC）

本番環境および Keycloak を使ったローカル検証では Bearer トークンが必要です。

```bash
# 1. トークンを取得（Resource Owner Password Credentials — ローカル開発用途のみ）
TOKEN=$(curl -s -X POST \
  'http://localhost:8080/realms/building-os/protocol/openid-connect/token' \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  -d 'grant_type=password&client_id=web-client&username=operator&password=operator' \
  | jq -r '.access_token')

# 2. API 呼び出し
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/buildings
```

本番環境では [Authorization Code + PKCE フロー](https://www.keycloak.org/docs/latest/securing_apps/)
を使用してください。
トークンの詳細（クレーム・スコープ）は [keycloak-user-management.md](keycloak-user-management.md) を参照してください。

### Swagger UI での試用

`http://localhost:5000/swagger` → **Authorize** → `bearerAuth` に取得したトークンを貼り付け。

---

## 2. リソース階層の取得

デジタルツインの設備階層（建物 → フロア → スペース → デバイス → ポイント）は
OxiGraph（SPARQL）で管理され、REST API を通じてアクセスします。

```bash
# 建物一覧
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/buildings

# 建物配下のフロア一覧
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/api/floors?buildingDtId=<building_dtId>"

# フロア配下のスペース一覧
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/api/spaces?floorDtId=<floor_dtId>"

# スペース配下のデバイス一覧
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/api/devices?spaceDtId=<space_dtId>"

# デバイス配下のポイント一覧
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/api/points?deviceDtId=<device_dtId>"
```

`dtId` は OxiGraph に登録されたリソースの識別子（例: `urn:nexus:building:site-a`）です。
URL クエリパラメータとして渡す際は `encodeURIComponent` でエンコードしてください。

### リソース横断検索

```bash
# テキスト検索（ポイント名・デバイス名など）
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/resources/search?q=temperature"

# カスタムタグで絞り込み
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/resources/search?customTags=HVAC,sensor"
```

---

## 3. テレメトリの読み取り

統一エンドポイント `GET /telemetries/query` が自動的に適切なストア層（Hot/Warm/Cold）を選択します。

### 最新値（Hot KV）

```bash
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/telemetries/query?pointId=<point_id>&latest=true"
```

レスポンス例:

```json
{
  "pointId": "urn:nexus:point:ahu-01-temp",
  "value": 22.5,
  "datetime": "2026-06-22T10:30:00Z",
  "unit": "celsius"
}
```

### 期間クエリ（Warm/Cold Parquet レイク）

```bash
# 過去 24 時間の時間集計
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5000/telemetries/query?pointId=<point_id>&start=2026-06-21T00:00:00Z&end=2026-06-22T00:00:00Z&granularity=Hour"
```

`granularity` の選択肢: `None`（生データ）/ `Hour` / `Day`

レスポンス例:

```json
[
  { "datetime": "2026-06-21T00:00:00Z", "value": 21.3 },
  { "datetime": "2026-06-21T01:00:00Z", "value": 21.8 }
]
```

### テレメトリの階層構造

| 層 | 鮮度 | バックエンド |
|----|------|------------|
| Hot | 即時（< 1 秒） | NATS KV |
| Warm | flush 間隔（既定 5 分） | MinIO Parquet |
| Cold | flush + compaction 後 | MinIO Parquet（圧縮済み） |

詳細は [oss-tier-architecture.md](oss-tier-architecture.md) と [oss-sla-freshness.md](oss-sla-freshness.md) を参照してください。

---

## 4. ポイント制御

点（point）への制御指令を送ります。制御は非同期で、結果は NATS 経由で返ります。

```bash
# 制御指令の送信 → 202 Accepted + controlId
curl -s -X POST -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  "http://localhost:5000/api/points/<point_id>/control" \
  -d '{"value": 1, "priority": 8}'
```

レスポンス:

```json
{ "controlId": "ctrl-uuid-xxxx" }
```

制御結果は gRPC streaming（`PointControlService`）で受け取ります。
Web Client では `useControlExecution()` フックが内部でこれを使用しています。
独自クライアントで gRPC-web を使う場合は `proto/point_control.proto` を参照してください。

**ゲートウェイがオフライン**の場合は 503 が即時に返ります（タイムアウト待ちなし）。

制御フローの詳細は [oss-control-safety.md](oss-control-safety.md) を参照してください。

---

## 5. ゲートウェイ管理 API

```bash
# ゲートウェイ一覧
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/gateways

# ゲートウェイのポイントリスト（ゲートウェイ側の point list 同期に使用）
curl -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/gateways/<gateway_id>/pointlist

# 変更があった場合のみ更新（ETag による条件 GET）
curl -H "Authorization: Bearer $TOKEN" \
     -H 'If-None-Match: "sha256:..."' \
  http://localhost:5000/api/gateways/<gateway_id>/pointlist
# → 変更なし: 304 Not Modified / 変更あり: 200 + 新しいポイントリスト
```

詳細は [gateway-integration.md](gateway-integration.md)（ETag・mTLS 認証）を参照してください。

---

## 6. システム状態の確認

```bash
# API サーバーと依存サービスの稼働状況
curl http://localhost:5000/api/system/status

# API サーバーの有効設定（管理者のみ）
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/system/config
```

---

## 7. SDK / 型生成

### TypeScript（web-client / Next.js）

web-client には Swagger から自動生成された Aspida 型定義が含まれています。
Swagger 仕様を更新した場合は以下で再生成してください。

```bash
# 1. Swagger を再生成（API Server 起動済みが前提）
cd Tools && ./generate_swagger.bash

# 2. フロントエンド型を同期
cd Tools && ./sync-type.bash

# 3. 型チェック
cd web-client && yarn typecheck
```

生成済みの型は `web-client/src/lib/infra/aspida-client/generated/` にあります。
**これらのファイルは手動編集しないでください。**

### その他の言語

`docs/schema/swagger.yaml` を OpenAPI Generator に渡すと、各言語の SDK を生成できます。

```bash
# 例: Python SDK
npx @openapitools/openapi-generator-cli generate \
  -i docs/schema/swagger.yaml \
  -g python \
  -o ./sdk-python
```

---

## 8. エラーレスポンス

| HTTP ステータス | 状況 |
|---------------|------|
| `200` / `202` | 成功（`202` は非同期制御の受理） |
| `400` | リクエストパラメータ不正 |
| `401` | 認証トークン未提供・無効 |
| `403` | 権限不足（ロール・パーミッション） |
| `404` | リソースが twin に未登録 |
| `503` | ゲートウェイがオフライン（制御 API） |

---

## 9. クイックリファレンス

| 目的 | エンドポイント |
|------|--------------|
| 建物一覧 | `GET /api/buildings` |
| フロア一覧 | `GET /api/floors?buildingDtId=` |
| スペース一覧 | `GET /api/spaces?floorDtId=` |
| デバイス一覧 | `GET /api/devices?spaceDtId=` |
| ポイント一覧 | `GET /api/points?deviceDtId=` |
| リソース検索 | `GET /resources/search?q=` |
| 最新テレメトリ | `GET /telemetries/query?pointId=&latest=true` |
| 期間テレメトリ | `GET /telemetries/query?pointId=&start=&end=&granularity=` |
| 制御指令 | `POST /api/points/{id}/control` |
| ゲートウェイ一覧 | `GET /api/gateways` |
| ポイントリスト | `GET /api/gateways/{id}/pointlist` |
| システム状態 | `GET /api/system/status` |

---

## 関連ドキュメント

- [`schema/swagger.yaml`](schema/swagger.yaml) — 完全な OpenAPI 3.0 仕様
- [keycloak-user-management.md](keycloak-user-management.md) — トークン取得・ユーザー管理
- [telemetry-specification.md](telemetry-specification.md) — テレメトリフィールド契約
- [oss-tier-architecture.md](oss-tier-architecture.md) — Hot/Warm/Cold 階層と API の選択基準
- [oss-control-safety.md](oss-control-safety.md) — 制御系の権限・安全分界・監査
- [gateway-integration.md](gateway-integration.md) — ゲートウェイ API・ETag・mTLS
- [standard-mapping.md](standard-mapping.md) — リソース表現（SBCO/Brick/REC/DTDL 対応表）
