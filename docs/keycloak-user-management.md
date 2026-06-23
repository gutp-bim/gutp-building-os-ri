# Keycloak ユーザー・認証管理ガイド

Building OS OSS における Keycloak のセットアップ、ユーザー作成、ロール付与、管理 UI の使い方を説明します。
認証設計の詳細（トークンクレーム・認可モデル）は [keycloak-permission-mapping.md](keycloak-permission-mapping.md)、
本番環境の初期化手順は [keycloak-admin-provisioning.md](keycloak-admin-provisioning.md) を参照してください。

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
| `operator` | `operator` | operator（読取 + 制御） |
| `viewer` | `viewer` | viewer（読取のみ） |

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
| `role` | `building-os-operator` | ロール識別子（トークンクレーム `building_os_role`） |
| `permissions` | `building:*:read` | 権限文字列（複数値は Add value で追加） |

5. **Role mapping** タブ → **Assign role** → 対象 realm ロールを選択

> **ポイント:** `building-os-api` クライアントスコープのプロトコルマッパーが
> `role` / `permissions` 属性をアクセストークンに埋め込みます。
> スコープ設定は `Clients → api-server → Client scopes` で確認できます。

### 3-b. Building OS 管理 UI から作成

Web Client の `/admin` ワークスペース（管理者ロールでログイン後）でもユーザー管理ができます。

1. `http://localhost:3000/admin/users` にアクセス
2. **ユーザーを追加** → ユーザー名・メール・パスワードを入力
3. **グループ** タブでグループへの追加、**権限** タブで個別パーミッションの付与が可能

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
building:*:read                          # 全建物の読取
floor:sha256-abc123:read                 # 特定フロアの読取（ID はハッシュ）
device:*:read,control                    # 全デバイスの読取・制御
point:sha256-xyz789:read,write,control   # 特定ポイントの読取・書込・制御
*:*:*                                    # 全リソースの全操作（admin）
```

`resourceId` にはデジタルツインの dtId の SHA-256 先頭 8 バイトが使われます
（ただしグループ ID は除く）。

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
  （[keycloak-admin-provisioning.md](keycloak-admin-provisioning.md) を参照）。

---

## 関連ドキュメント

- [keycloak-permission-mapping.md](keycloak-permission-mapping.md) — トークンクレームと `AuthorizationContext` のマッピング詳細
- [keycloak-admin-provisioning.md](keycloak-admin-provisioning.md) — realm import と Admin API クライアントの運用手順
- [api-client-guide.md](api-client-guide.md) — Bearer トークンを使った API 呼び出し
- [system-architecture.md](system-architecture.md) — 全体セキュリティモデル
