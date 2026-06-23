# Building OS 開発・運用支援ツール

このディレクトリには、Building OS の開発・運用を支援するツール群が格納されています。

## 📁 ツール一覧

| ツール | 説明 |
|--------|------|
| `auth-proxy-server/` | ローカル開発用認証プロキシサーバー |
| `development-edge-device/` | IoT デバイスシミュレータ |
| `workload-test-project/` | 負荷試験ツール |
| `generate_adt_point_csv.py` | Azure Digital Twins ポイント CSV 生成 |
| `generate_swagger.bash` | Swagger/OpenAPI 定義生成 |
| `generate-dotnet-entities-from-schema.bash` | JSON Schema からエンティティクラス生成 |
| `sync-type.bash` | フロントエンド型定義の同期 |
| `build-and-push-api-server.bash` | API Server のビルド＆プッシュ |
| `request-event-to-local.py` | ローカル環境へのイベント送信 |

## 🔧 主要ツール

### 1. 認証プロキシサーバー (`auth-proxy-server/`)

ローカル開発時に Azure AD 認証をバイパスするためのプロキシサーバー。

```bash
cd auth-proxy-server
pip install -r requirements.txt
cp env.template .env
# .env を編集してから
python proxy_server.py
```

詳細は [auth-proxy-server/README.md](auth-proxy-server/README.md) を参照してください。

### 2. 開発用デバイスシミュレータ (`development-edge-device/`)

IoT Hub へメッセージを送信するデバイスシミュレータ。

```bash
cd development-edge-device

# シンプルなデバイスシミュレータ
python iot_simple_device.py

# エッジデバイスシミュレータ
python iot_edge_device.py
```

### 3. 負荷試験ツール (`workload-test-project/`)

Building OS の性能限界を測定するための負荷試験ツール。

```bash
cd workload-test-project
pip install -r requirements.txt

# デバイススケーリング試験
python scripts/run_test.py --scenario device_scaling --step all

# メッセージ頻度試験
python scripts/run_test.py --scenario message_frequency --step 1,2

# データサイズ負荷試験
python scripts/run_test.py --scenario data_size_load --step all
```

詳細は [workload-test-project/README.md](workload-test-project/README.md) を参照してください。

## 🛠️ コード生成ツール

### JSON Schema からエンティティクラス生成

`BuildingOS.Shared/Defines/Schemas/` の JSON Schema から C# エンティティクラスを自動生成します。

```bash
# MSYS2/Bash 環境で実行
./generate-dotnet-entities-from-schema.bash
```

生成されたクラスは `BuildingOS.Shared/Defines/Entities/` に配置されます。

### Swagger/OpenAPI 定義生成

API Server から Swagger 定義を生成します。

```bash
# API Server が起動している状態で実行
./generate_swagger.bash
```

生成された定義は `docs/schema/swagger.yaml` に保存されます。

### フロントエンド型定義の同期

API Server の Swagger 定義から、フロントエンド用の型定義を生成します。

```bash
./sync-type.bash
```

型定義は `web-client/src/lib/infra/aspida-client/generated/` に生成されます。

## 📊 データ生成ツール

### Azure Digital Twins ポイント CSV 生成

Digital Twins のポイント情報を CSV 形式で出力します。

```bash
python generate_adt_point_csv.py
```

出力ファイル: `adt_points.csv`

## 🚀 デプロイツール

### API Server のビルド＆プッシュ

API Server の Docker イメージをビルドし、Azure Container Registry にプッシュします。

```bash
./build-and-push-api-server.bash
```

## 🧪 テストツール

### ローカル環境へのイベント送信

IoT Hub からのイベントをローカルの Functions に送信するテストツール。

```bash
python request-event-to-local.py
```

### 環境変数

各ツールは環境変数を使用します。`.env` ファイルまたはシステム環境変数で設定してください。

### Azure リソースへの接続

多くのツールは Azure リソース（IoT Hub、Storage、CosmosDB 等）への接続文字列が必要です。本番環境への影響に注意してください。

## 🤝 コントリビューション

新しいツールを追加する場合：

1. ツール用のディレクトリを作成
2. README.md を作成して使い方を記載
3. このファイルのツール一覧に追加

## 📚 参考資料

- [Azure IoT Hub ドキュメント](https://learn.microsoft.com/ja-jp/azure/iot-hub/)
- [Azure Digital Twins ドキュメント](https://learn.microsoft.com/ja-jp/azure/digital-twins/)
- [OpenAPI Specification](https://swagger.io/specification/)

