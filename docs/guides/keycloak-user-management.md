# Keycloak ユーザー・認証管理ガイド

Building OS OSS における Keycloak のセットアップ、ユーザー作成、ロール付与、管理 UI の使い方を説明します。
認証設計の詳細（トークンクレーム・認可モデル）は [keycloak-permission-mapping.md](../operations/keycloak-permission-mapping.md)、
本番環境の初期化手順は [keycloak-admin-provisioning.md](../operations/keycloak-admin-provisioning.md) を参照してください。

---

## 1. ローカル開発での認証

### 1-a. 認証をスキップする（最も手軽）

API Server と ConnectorWorker には `DISABLE_AUTH=true` 環境変数が用意されており、
JWT 検証をバイパスしてすべてのリクエストを管理者として扱います。

```bash
# API Server を認証なしで起動
cd DotNet/BuildingOS.ApiServer
DISABLE_AUTH=true dotnet run --launch-profile WithLocal
```

`docker-compose.oss.yaml` の API Server サービスにも同変数を設定できます。
**本番環境では絶対に使用しないでください。**

### 1-b. Keycloak を使ってログインする

Docker Compose で起動した Keycloak（`http://localhost:8080`）にはデモ用の
realm とユーザーが自動的にインポートされています（`oss-stack/keycloak/realm.json`）。

初期アカウント:

| ユーザー名 | パスワード | ロール |
|------------|------------|--------|
| `admin` | `admin` | admin（全操作可） |

> `realm.json` に含まれるのはこの 1 アカウントのみです（operator / viewer は既定では未作成——
> 必要なら「3. ユーザーの作成」で、リソースごとの `permissions` を付けて追加してください）。
> ラボ/CI 専用の既定資格情報です。

Web Client（`http://localhost:3000`）にアクセスすると Keycloak ログイン画面にリダイレクトされます。

---

## 2. Keycloak 管理コンソール

`http://localhost:8080/admin`（管理者: `admin` / `admin`）からアクセスします。

左サイドバーで **realm: building-os** を選択してください（`master` realm ではありません）。

### 主要メニュー

| メニュー | 目的 |
|---------|------|
| Users | ユーザーの作成・編集・パスワードリセット |
| Groups | グループの作成・ユーザー追加 |
| Realm roles | ロールの確認（`building-os-admin` / `building-os-operator` / `building-os-viewer`） |
| Clients | `web-client`（公開クライアント）・`api-server`（機密クライアント）の設定 |
| Client scopes | `building-os-api` スコープ（ロール・権限クレームのマッパー） |

---

## 3. ユーザーの作成

### 3-a. 管理コンソールから作成

1. **Users** → **Add user** をクリック
2. `Username` を入力し **Save**
3. **Credentials** タブ → **Set password**（`Temporary: OFF` にして確定）
4. **Attributes** タブで以下を設定:

| Key | Value の例 | 説明 |
|-----|-----------|------|
| `role` | `operator` | ロール識別子 `admin` / `operator` / `viewer`（トークンクレーム `building_os_role`） |
| `permissions` | `b:<56桁hex>:r` / `group:tenant-a:read` | 権限文字列（複数値は Add value で追加。ワイルドカード不可） |

5. **Role mapping** タブ → **Assign role** → 対象 realm ロールを選択

> **ポイント:** `building-os-api` クライアントスコープのプロトコルマッパーが
> `role` / `permissions` 属性をアクセストークンに埋め込みます。
> スコープ設定は `Clients → api-server → Client scopes` で確認できます。

### 3-b. Building OS 管理 UI から作成

Web Client の `/admin` ワークスペース（管理者ロールでログイン後）でもユーザー管理ができます。

1. `http://localhost:3000/admin/users` にアクセス
2. **ユーザーを追加** → ユーザー名・メール・パスワードを入力
3. **グループ** タブでグループへの追加、**権限** タブで個別パーミッションの付与が可能

> **属性名（#519）:** 管理 UI が読み書きするのは 3-a と同じ `role` / `permissions` 属性です
> （トークンのマッパーが読む属性。名前の定義は `KeycloakUserAttributes` の 1 か所）。
> 管理 UI で付けた権限はそのままトークンの `permissions` クレームに載り、所属グループの
> `permissions` とも合算されます（#508）。
>
> #519 より前の管理 UI は `buildingos_role` / `buildingos_permissions` に書いていたため、
> その権限はトークンに載っていませんでした。移行期間中は Admin API 経路で旧属性も読み合わせ
> （role は `role` 優先・空なら `buildingos_role`、permissions は両方の和集合）、管理 UI で
> そのユーザーを更新すると新属性に書き込んで旧属性を削除します。既存ユーザーの一括移行
> （`kcadm.sh` + `jq`）と、realm のユーザープロファイル設定（`unmanagedAttributePolicy: ADMIN_EDIT`
> — これが無いと Keycloak 24+ は `role` / `permissions` を黙って捨てる）は
> [keycloak-permission-mapping.md の「Admin UI writes the same attributes」](../operations/keycloak-permission-mapping.md#admin-ui-writes-the-same-attributes-519)
> を参照してください。

---

## 4. ロールと権限モデル

Building OS は Keycloak のロール（粗粒度）と権限文字列（細粒度）の 2 層で認可を管理します。

### ロール一覧

| Keycloak realm ロール | 説明 | トークンクレーム `building_os_role` |
|----------------------|------|--------------------------------------|
| `building-os-admin` | 全操作（ユーザー管理・設備登録・制御） | `admin` |
| `building-os-operator` | 読取 + 制御 | `operator` |
| `building-os-viewer` | 読取のみ | `viewer` |

### 権限文字列フォーマット

```
{resourceType}:{resourceId}:{actions}
```

例:

```
building:<hash>:read                     # 特定建物（と配下）の読取
point:<hash>:read,write                  # 特定ポイントの読取・書込（制御は write で判定）
group:tenant-a:read                      # Group tenant-a に登録されたリソースの読取
```

`resourceId` は SHA-256 の先頭 28 バイト（hex 56 文字）でハッシュ化して保存・比較します
（ただしグループ ID は除く）。`/admin` の権限タブから付与すると、生の ID が自動でハッシュ化されます。

**型・ID は完全一致のみです。`*` はワイルドカードとして解釈されません**（`building:*:read` のような
権限は何も許可せず、API サーバが警告ログを出して無視します。#505）。全件アクセスは admin ロール
（`role=admin`）で表します。

---

## 5. グループ管理

複数ユーザーに同一の権限セットを付与する場合はグループを使います。

1. **Groups** → **Create group**（例: `building-a-operators`）
2. グループの **Attributes** に `role` と `permissions` を設定
3. ユーザーの **Groups** タブから対象グループに追加

グループ属性はメンバー全員のトークンに反映されます。

---

## 6. トークンの取得（API テスト用）

`DISABLE_AUTH=true` を使わず実際に Keycloak トークンを取得して API を呼ぶ場合:

```bash
# Resource Owner Password Credentials（ローカル開発用途のみ）
TOKEN=$(curl -s -X POST 'http://localhost:8080/realms/building-os/protocol/openid-connect/token' \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  -d 'grant_type=password' \
  -d 'client_id=web-client' \
  -d 'username=operator' \
  -d 'password=operator' \
  | jq -r '.access_token')

# API 呼び出し
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/buildings
```

Swagger UI（`http://localhost:5000/swagger`）から試す場合は、
**Authorize** ボタン → `bearerAuth` に上記トークンを貼り付けてください。

---

## 7. 本番環境での注意事項

- `realm.json` に含まれる `api-server` クライアントシークレットは**ローカル開発用プレースホルダー**です。
  本番では Keycloak 管理コンソールまたは Kubernetes Secret で上書きしてください。
- `KEYCLOAK_ADMIN_CLIENT_SECRET` は CI/CD シークレットとして管理し、ソースコードにコミットしないこと。
- 初期 admin ユーザー（`admin`/`admin`）のパスワードは本番起動前に必ず変更してください。
- realm の変更は `realm.json` をソース管理に反映したうえで、レビュー → 反映のフローを踏んでください
  （[keycloak-admin-provisioning.md](../operations/keycloak-admin-provisioning.md) を参照）。

---

## 関連ドキュメント

- [keycloak-permission-mapping.md](../operations/keycloak-permission-mapping.md) — トークンクレームと `AuthorizationContext` のマッピング詳細
- [keycloak-admin-provisioning.md](../operations/keycloak-admin-provisioning.md) — realm import と Admin API クライアントの運用手順
- [api-client-guide.md](api-client-guide.md) — Bearer トークンを使った API 呼び出し
- [system-architecture.md](../architecture/system-architecture.md) — 全体セキュリティモデル
