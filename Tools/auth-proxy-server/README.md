# Azure EntraID 認証プロキシサーバー

BuildingOS API Server に対して、Azure EntraID の認証ヘッダーを自動で付与するプロキシサーバーです。
開発・デバッグ時に面倒な認証ヘッダーを手動で設定する必要がなくなります。

## 概要

このツールは以下の動作を行います：

1. **起動時**: Azure EntraID から認証トークンを自動取得
2. **リクエスト時**: クライアントからのリクエストに`Authorization: Bearer <token>`ヘッダーを自動付与
3. **転送**: 認証ヘッダー付きでバックエンド API にリクエストを転送
4. **レスポンス**: バックエンドからのレスポンスをそのままクライアントに返却

## セットアップ

### 1. 依存関係のインストール

```bash
cd Tools/auth-proxy-server
pip install -r requirements.txt
```

### 2. 環境変数の設定

`env.template`をコピーして`.env`ファイルを作成し、必要な情報を入力してください：

```bash
cp env.template .env
```

`.env`ファイルの設定例：

```env
# Azure EntraID認証設定
AZURE_TENANT_ID=12345678-1234-1234-1234-123456789abc
AZURE_CLIENT_ID=87654321-4321-4321-4321-987654321abc
AZURE_USERNAME=your-email@example.com
AZURE_PASSWORD=your-secure-password

# バックエンドAPI URL
BACKEND_API_URL=https://your-api.azurewebsites.net

# プロキシサーバー設定（オプション）
PROXY_HOST=127.0.0.1
PROXY_PORT=8080
```

#### 必須環境変数

| 変数名            | 説明                                         |
| ----------------- | -------------------------------------------- |
| `AZURE_TENANT_ID` | Azure EntraID のテナント ID                  |
| `AZURE_CLIENT_ID` | アプリケーション（クライアント）ID           |
| `AZURE_USERNAME`  | Azure EntraID のユーザー名（メールアドレス） |
| `AZURE_PASSWORD`  | Azure EntraID のパスワード                   |
| `BACKEND_API_URL` | 接続先の Building OS API サーバーの URL      |

#### オプション環境変数

| 変数名        | デフォルト値                 | 説明                     |
| ------------- | ---------------------------- | ------------------------ |
| `AZURE_SCOPE` | `{AZURE_CLIENT_ID}/.default` | 認証スコープ             |
| `PROXY_HOST`  | `127.0.0.1`                  | プロキシサーバーのホスト |
| `PROXY_PORT`  | `8080`                       | プロキシサーバーのポート |

### 3. Azure EntraID の設定

このツールは**Resource Owner Password Credentials (ROPC) フロー**を使用します。
本番環境では推奨されませんが、開発ツールとしては便利です。

Azure EntraID で以下の設定が必要です：

1. **アプリケーション登録**で ROPC フローを有効化
2. **認証** → **詳細設定** → **パブリック クライアント フローを許可する**: `はい`

## 使い方

### プロキシサーバーの起動

#### 方法 1: 起動スクリプトを使用（推奨）

**Linux/Mac/MSYS2:**

```bash
cd Tools/auth-proxy-server
./start.sh
```

**Windows (コマンドプロンプト):**

```cmd
cd Tools\auth-proxy-server
start.bat
```

起動スクリプトは自動で依存関係をチェックし、必要に応じてインストールします。

#### 方法 2: 直接 Python で起動

```bash
cd Tools/auth-proxy-server
python proxy_server.py
```

起動すると以下のようなログが表示されます：

```
============================================================
🚀 Azure EntraID認証プロキシサーバー起動
============================================================
プロキシサーバー: http://127.0.0.1:8080
バックエンドURL: https://your-api.azurewebsites.net
テナントID: 12345678-1234-1234-1234-123456789abc
クライアントID: 87654321-4321-4321-4321-987654321abc
ユーザー: your-email@example.com
============================================================
✅ 初回トークン取得成功
```

### API リクエスト

プロキシサーバー経由で API にアクセスします：

```bash
# 例：ヘルスチェック
curl http://localhost:8080/health

# 例：Building一覧取得
curl http://localhost:8080/buildings

# 例：特定のBuilding情報取得
curl http://localhost:8080/buildings/building-001

# 例：Device一覧取得
curl http://localhost:8080/buildings/building-001/devices

# 例：Point詳細取得
curl http://localhost:8080/points/point-123/detail

# 例：Telemetryデータ取得（クエリパラメータ付き）
curl "http://localhost:8080/telemetry/points/point-123?startDate=2024-01-01&endDate=2024-01-31"

# 例：POSTリクエスト（Point制御）
curl -X POST http://localhost:8080/control/points/point-123 \
  -H "Content-Type: application/json" \
  -d '{"value": 25.0, "priority": 8}'
```

**認証ヘッダーを手動で設定する必要はありません**。プロキシサーバーが自動で付与します。

### Web クライアントから使用

Web Client の API エンドポイントをプロキシサーバーに向けることもできます：

```typescript
// 例：web-client/.env.local
NEXT_PUBLIC_API_BASE_URL=http://localhost:8080
```

### プロキシサーバーのテスト

プロキシサーバーが正常に動作しているか確認するためのテストスクリプトを用意しています。

```bash
# 基本的なテスト
python test_proxy.py

# カスタムプロキシURLを指定
python test_proxy.py http://localhost:8080

# カスタムエンドポイントもテスト
python test_proxy.py http://localhost:8080 /buildings/building-001
```

テストスクリプトは以下を確認します：

- プロキシサーバーへの接続
- ヘルスチェックエンドポイント
- Building 一覧エンドポイント（認証が必要）
- カスタムエンドポイント（オプション）

## トラブルシューティング

### トークン取得エラー

```
❌ トークン取得失敗: 401 Unauthorized
```

**原因と対処法**：

- ユーザー名・パスワードが間違っている → `.env`を確認
- ROPC フローが有効になっていない → Azure Portal で設定確認
- テナント ID またはクライアント ID が間違っている → `.env`を確認

### バックエンド接続エラー

```
❌ バックエンドリクエストエラー: Connection refused
```

**原因と対処法**：

- バックエンド API が起動していない → API サーバーを起動
- `BACKEND_API_URL`が間違っている → `.env`を確認

### ポートが使用中

```
OSError: [Errno 48] Address already in use
```

**対処法**：

- `.env`の`PROXY_PORT`を変更（例：8081）
- 既存のプロセスを終了

## セキュリティに関する注意

⚠️ **重要**: このツールは開発・デバッグ用です。以下の点に注意してください：

1. **パスワードを.env ファイルに平文保存** → `.env`ファイルを`.gitignore`に追加
2. **ROPC フローの使用** → 本番環境では使用しない
3. **ローカルホストのみで使用** → 外部ネットワークに公開しない

## ライセンス

このツールは Building OS プロジェクトの一部として提供されています。
