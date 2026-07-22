# リソース管理ガイド — インポート・更新・削除・アクセス制御

デジタルツイン（OxiGraph / SPARQL）に登録するリソース（建物→フロア→スペース→機器→ポイント）の
管理手順を説明します。インポート・更新・削除・複数ビル対応・ロールベースの表示制御を扱います。

> **前提:** OxiGraph は Building OS のデジタルツインエンジンです。全リソースは RDF（Turtle 形式）
> として保管され、ゲートウェイの接続・テレメトリの関連付け・制御経路の解決はすべてこの twin を
> 起点に行われます。SPARQL ツールとして `GET /api/admin/twin/query` も利用できます。

---

## 1. Turtle ファイルの構造

Building OS は **SBCO オントロジー**（`sbco:`）をリソース記述に使用します。典型的な Turtle の構造を示します。

```turtle
@prefix sbco: <https://www.sbco.or.jp/ont/> .
@prefix bos:  <https://building-os.example.com/ontology#> .
@prefix xsd:  <http://www.w3.org/2001/XMLSchema#> .

# ── ビル ──────────────────────────────────────────────────────────────────
<https://example.com/bldg/bldg-1> a sbco:Building ;
    sbco:name     "第1ビル" ;
    sbco:building "bldg-1" .     # 短縮 ID（PointExt の sbco:building が参照する値）

# ── フロア ────────────────────────────────────────────────────────────────
<https://example.com/level/lv-1> a sbco:Level ;
    sbco:name     "1階" ;
    sbco:building <https://example.com/bldg/bldg-1> .

# ── スペース ──────────────────────────────────────────────────────────────
<https://example.com/room/rm-1> a sbco:Room ;
    sbco:name  "会議室 A" ;
    sbco:level <https://example.com/level/lv-1> .

# ── 機器（EquipmentExt） ──────────────────────────────────────────────────
<https://example.com/equip/eq-1> a sbco:EquipmentExt ;
    sbco:name       "空調機 1" ;
    sbco:deviceType "AHU" ;
    sbco:room       <https://example.com/room/rm-1> ;
    sbco:gatewayId  "GW001" .   # ゲートウェイ ID（省略可）

# ── ポイント（PointExt） ─────────────────────────────────────────────────
<https://example.com/point/pt-1> a sbco:PointExt ;
    sbco:name           "室温" ;
    sbco:unit           "degC" ;
    sbco:writable       "false"^^xsd:boolean ;
    sbco:localId        "PT-TEMP-001" ;          # ゲートウェイ側のローカル識別子
    sbco:gatewayId      "GW001" ;                # このポイントを持つゲートウェイ
    sbco:building       "bldg-1" ;               # 短縮ビル ID（必須：ingress での建物解決に使用）
    sbco:equipment      <https://example.com/equip/eq-1> ;

    # BACnet フィールド（BACnet ゲートウェイの場合）
    sbco:deviceIdBacnet "1001" ;
    sbco:objectTypeBacnet "analogInput" ;
    sbco:instanceNoBacnet "0"^^xsd:integer ;

    # 制御スキーマ（writable=true のポイントに設定）
    bos:controlSchema   "{\"dataType\":\"number\",\"min\":16,\"max\":28,\"step\":0.5}" .
```

> **`sbco:building` リテラルは必須です。** OxiGraph の ingress メタデータ解決（`IPointMetadataCache`）が
> `point_id` から建物を引く際にこのフィールドを使用します。省略するとテレメトリ受信が失敗します。

---

## 2. インポート方法

### 方法 A — 起動時シードファイル

環境変数 `OXIGRAPH_SEED_TTL_PATH` にファイルパスを設定すると、**API Server / ConnectorWorker の
起動ごとに**デフォルトグラフを全置換します。

```bash
# API Server をシードと共に起動
OXIGRAPH_SEED_TTL_PATH=/path/to/twin.ttl \
  dotnet run --project DotNet/BuildingOS.ApiServer --launch-profile WithLocal

# docker-compose で設定する場合
# docker-compose.oss.yaml の environment セクションに追加:
#   OXIGRAPH_SEED_TTL_PATH: /data/twin.ttl
# + volumes でファイルをマウント
```

> **注意:** `ReplaceDefaultGraphAsync` は毎起動 OxiGraph のデフォルトグラフを**全置換**します。
> 起動するたびに TTL の内容で上書きされるため、OxiGraph に直接加えた変更は再起動で消えます。
> ローカル開発の初期データ投入用途向きです。

シード完了後に自動で:
1. `gateway_id` 一意性を検証（複数ビルにまたがると起動停止）
2. 各ゲートウェイに point-list 更新シグナルを送信（GatewayBridge 経由）

---

### 方法 B — 管理 UI（`/admin/twin`）

Web クライアントの `/admin/twin` から Turtle ファイルをアップロードできます。
**管理者のみ**操作でき、すべての操作が監査ログに記録されます。

**推奨手順:**

1. `/admin/twin` を開く（`http://localhost:3000/admin/twin`）
2. Turtle ファイルを選択して「プレビュー」を実行
3. プレビュー結果でトリプル件数・ゲートウェイ数・エラーを確認
4. モード（`append` / `replace`）を選択して「適用」

---

### 方法 C — REST API（直接 curl）

```bash
# ─── プレビュー（適用なし） ───────────────────────────────────────────────
curl -X POST http://localhost:5000/api/admin/twin/import/preview \
  -H "Content-Type: application/json" \
  -d "{\"turtle\": $(cat twin.ttl | jq -Rs .)}"

# レスポンス例:
# {
#   "tripleCount": 42,
#   "gatewayCount": 2,
#   "valid": true,
#   "gatewayConflicts": []
# }

# ─── 追記適用 ────────────────────────────────────────────────────────────
curl -X POST http://localhost:5000/api/admin/twin/import/apply \
  -H "Content-Type: application/json" \
  -d "{\"turtle\": $(cat twin.ttl | jq -Rs .), \"mode\": \"append\"}"

# ─── 全置換適用 ──────────────────────────────────────────────────────────
curl -X POST http://localhost:5000/api/admin/twin/import/apply \
  -H "Content-Type: application/json" \
  -d "{\"turtle\": $(cat twin.ttl | jq -Rs .), \"mode\": \"replace\"}"
```

> 認証が有効な環境（`DISABLE_AUTH=false`）では Bearer トークンが必要です。
> `keycloak-user-management.md` のトークン取得手順を参照してください。

---

## 3. 重複・上書きの挙動

| 操作 | 既存リソースへの影響 |
|------|---------------------|
| 起動時シード（`OXIGRAPH_SEED_TTL_PATH`） | デフォルトグラフを**全削除→再投入**。必ず上書き |
| `mode: replace` | 同上（全削除→再投入） |
| `mode: append`（省略時） | 既存トリプルに追記。同一トリプルは無視（RDF セマンティクス上の upsert）|

**`append` 時の注意点:**

既存ポイントの `sbco:name` を変更したい場合、古いトリプル（`<pt-1> sbco:name "旧名称"`）が残ります。
フィールドの書き換えには `replace` モードを使うか、旧トリプルを除いた完全な TTL で置き換えてください。

---

## 4. gateway_id の一意性制約

1 つの `gateway_id` は **1 つのビルにのみ所属**できます。異なるビルにまたがる場合:

- **起動時シード** → `InvalidOperationException` で起動停止
- **REST API** (`/import/apply`) → プレビュー段階で `valid: false` を返し **409 Conflict** で拒否

複数ゲートウェイを管理する場合は `gateway_id` をビル単位でユニークにしてください（例: `GW-BLDG1-001`、`GW-BLDG2-001`）。

```sparql
-- 現在の gateway 一覧を確認する SPARQL（/admin/twin の SPARQL コンソールで実行可能）
PREFIX sbco: <https://www.sbco.or.jp/ont/>
SELECT DISTINCT ?gatewayId ?building WHERE {
  ?point a sbco:PointExt ;
         sbco:gatewayId ?gatewayId ;
         sbco:building ?building .
}
ORDER BY ?building ?gatewayId
```

---

## 5. 複数ビル・複数ゲートウェイの表示

### 管理者（IsAdmin = true）

**全ビル・全ゲートウェイが表示されます。** `AuthorizedTwinView` が `IsAdmin` を判定し、
フィルタなしで OxiGraph の全データを返します。

- `/resources` — ビルツリーで全ビルが表示
- `/admin/gateways` — TTL 内の全 `gateway_id` が一覧
- `/admin/twin` — SPARQL コンソールで全トリプルを参照可能

### 非管理者ユーザー

パーミッション文字列で制御されたビルのみが表示されます（次節参照）。

---

## 6. ロールベースのビル表示制御

非管理者ユーザーには、`building:read` パーミッションを持つビルのみが `/resources` に表示されます。

### パーミッション文字列の形式

```
b:{sha256先頭56文字(hex)}:r
```

ビル DtId（例: `https://example.com/bldg/bldg-1`）を SHA-256 でハッシュし、先頭 28 バイト（56 文字 hex）をキーとします。

### 設定方法

1. `/admin/users/{userId}` または `/admin/groups/{groupId}` の権限設定ページを開く
2. 対象ビルの `building:read` 権限を付与

権限は以下の継承ルールで解決されます:

| 権限対象 | 効果 |
|---------|------|
| `building` | そのビルと配下の全フロア・スペース・機器・ポイントにアクセス可能 |
| `floor` | そのフロアと配下のスペース・機器・ポイントにアクセス可能 |
| `space` | そのスペースと配下の機器・ポイントにアクセス可能 |
| `device` | その機器と配下のポイントにアクセス可能 |

> 祖先チェーン解決（`IResourceHierarchyResolver`）が自動で行われるため、
> ビルの権限だけで配下リソース全体にアクセスできます。

### curl でのパーミッション文字列生成例（Python）

```python
import hashlib

def building_permission(dt_id: str) -> str:
    h = hashlib.sha256(dt_id.encode()).hexdigest()[:56]
    return f"b:{h}:r"

print(building_permission("https://example.com/bldg/bldg-1"))
# 例: b:3f7a1c...（56文字hex）:r
```

---

## 7. リソースの削除

### 現状の制約

**個別リソースの削除 API および削除 UI は現時点で実装されていません。**
建物・フロア・スペース・機器・ポイントの DELETE エンドポイントは存在せず、
`IDigitalTwinDatabase` にも削除系メソッドはありません。

### 削除の実施方法（`replace` インポートによる全置換）

削除したいリソースを除いた完全な Turtle ファイルを用意して `mode: replace` でインポートします。

```bash
# 手順:
# 1. 現在の TTL を SPARQL で取得するか、管理 TTL ファイルを手元で管理する
# 2. 削除対象のリソースに関するトリプルを TTL から削除
# 3. replace モードで適用

curl -X POST http://localhost:5000/api/admin/twin/import/apply \
  -H "Content-Type: application/json" \
  -d "{\"turtle\": $(cat twin_without_deleted.ttl | jq -Rs .), \"mode\": \"replace\"}"
```

> **ポイント削除の影響:** ポイントを削除すると、そのポイントの `point_id` を指定したテレメトリ
> ingress は「未知の point_id」として skipped になります（ログ + メトリクス）。Parquet レイクに
> 蓄積済みのテレメトリデータは削除されません。

---

## 8. ポイントリストの同期

ゲートウェイは `GET /gateways/{gatewayId}/pointlist` でポイントリストを取得します。
twin インポート後にゲートウェイへ即時反映させる方法:

```bash
# ゲートウェイに pointlist の再同期を要求
curl -X POST "http://localhost:5000/api/admin/gateways/{gatewayId}/pointlist/resync"
```

または twin のインポートが完了すると自動的に NATS 経由で push 通知が飛び（`building-os.pointlist.updated.gw.{id}`）、
GatewayBridge を通じてゲートウェイが再取得します。詳細は [oss-gateway-pointlist-sync.md](../architecture/oss-gateway-pointlist-sync.md)。

---

## 9. よくある問題

| 症状 | 原因 | 対処 |
|------|------|------|
| インポートが 409 | `gateway_id` が複数ビルに存在 | TTL の `sbco:gatewayId` と `sbco:building` の対応を修正してから再インポート |
| テレメトリが ingress で skipped | `sbco:building` リテラルが未設定、または `point_id` が twin 未登録 | TTL に `sbco:building` を追加し再インポート |
| フロント `/resources` でビルが見えない | 非管理者ユーザーに `building:read` 権限なし | `/admin/users/{id}` で権限付与、または管理者でログイン |
| 再起動するたびにデータが戻る | `OXIGRAPH_SEED_TTL_PATH` が設定されていて毎回全置換 | シードファイルを最新状態に保つか、API 方式（`/admin/twin/import`）に移行 |
| append で名前が変わらない | 古いトリプルが残存 | `replace` モードで全置換するか、SPARQL DELETE + INSERT で直接書き換え（`/admin/twin/query` は SELECT/ASK のみのため API 外の SPARQL クライアントが必要）|

---

## 関連ドキュメント

- [gateway-integration.md](gateway-integration.md) — ゲートウェイ接続モデル・point list 同期・mTLS
- [oss-gateway-pointlist-sync.md](../architecture/oss-gateway-pointlist-sync.md) — point list 同期の詳細設計
- [oss-sparql-mapping.md](../architecture/oss-sparql-mapping.md) — SBCO/RDF の詳細マッピング・SPARQL クエリ例
- [keycloak-user-management.md](keycloak-user-management.md) — ユーザー・ロール・権限設定
- [standard-mapping.md](../architecture/standard-mapping.md) — SBCO / bos: 語彙と Brick / REC / IFC / DTDL の対応
