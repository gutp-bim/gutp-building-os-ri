# Building OS 負荷試験システム

Building OS の本番運用前性能検証のための負荷試験ツールです。Azure IoT Hub 経由での大量デバイスシミュレーションと性能限界の特定を行います。

## 概要

本ツールは負荷試験計画書（`docs/test/workload-test/workload-test-plan.md`）の 3 つのシナリオを実行します：

1. **デバイス数スケーリング試験**: 250 台 →5000 台への段階的負荷増加
2. **メッセージ頻度ストレス試験**: 60 秒 →5 秒間隔への送信頻度向上
3. **データサイズ負荷試験**: 10→100 ポイントへのメッセージサイズ拡大

## 必要環境

- **Python 3.11+**
- **Azure IoT Hub**: テスト用 IoT Hub インスタンス
- **Docker** (オプション): コンテナ実行時

## セットアップ

### 1. 依存関係インストール

```bash
pip install -r requirements.txt
```

### 2. 環境変数設定

`.env` ファイルを作成：

```bash
# 基本接続文字列
IOTHUB_CONNECTION_STRING="HostName=your-iothub.azure-devices.net;SharedAccessKeyName=iothubowner;SharedAccessKey=..."

# デバイス別接続文字列
BACNET_DEVICE_CONNECTION_STRING="HostName=your-iothub.azure-devices.net;DeviceId=loadtest-bacnet-{id};SharedAccessKey=..."
HVAC_DEVICE_CONNECTION_STRING="HostName=your-iothub.azure-devices.net;DeviceId=loadtest-hvac-{id};SharedAccessKey=..."
ENV_DEVICE_CONNECTION_STRING="HostName=your-iothub.azure-devices.net;DeviceId=loadtest-env-{id};SharedAccessKey=..."
ELECTRIC_DEVICE_CONNECTION_STRING="HostName=your-iothub.azure-devices.net;DeviceId=loadtest-electric-{id};SharedAccessKey=..."
BEHAVIOR_DEVICE_CONNECTION_STRING="HostName=your-iothub.azure-devices.net;DeviceId=loadtest-behavior-{id};SharedAccessKey=..."

# Azure Monitor メトリクス収集用（オプション）
AZURE_SUBSCRIPTION_ID="your-subscription-id"
AZURE_TENANT_ID="your-tenant-id"
AZURE_CLIENT_ID="your-client-id"
AZURE_CLIENT_SECRET="your-client-secret"

FUNCTION_RESOURCE_GROUP="your-function-resource-group"
FUNCTION_APP_NAME="your-function-app-name"

COSMOSDB_RESOURCE_GROUP="your-cosmosdb-resource-group"
COSMOSDB_ACCOUNT_NAME="your-cosmosdb-account"

IOTHUB_RESOURCE_GROUP="your-iothub-resource-group"
IOTHUB_NAME="your-iothub-name"
```

### 3. Azure IoT Hub デバイス登録

```bash
# デバイス一括登録スクリプト実行
python scripts/setup_devices.py --scenario device_scaling --max-devices 1000
```

## 基本的な使用方法

### クイックスタート

```bash
# 小規模テスト（10分間）
python scripts/run_test.py --scenario device_scaling --step 1 --duration 10 --debug

# 全シナリオ実行
python scripts/run_test.py --scenario device_scaling --step all
```

### よく使用するコマンド

```bash
# 特定ステップのみ実行
python scripts/run_test.py --scenario device_scaling --step 1,2

# 特定デバイスタイプのみ
python scripts/run_test.py --scenario message_frequency --device-types bacnet,hvac

# メッセージ頻度テスト（短時間）
python scripts/run_test.py --scenario message_frequency --step 1,2 --duration 15

# データサイズテスト
python scripts/run_test.py --scenario data_size_load --step all
```

## Docker 実行

```bash
# Docker でのテスト実行
docker-compose up

# 複数コンテナでの並列実行
docker-compose up --scale loadtest=3
```

## メトリクス監視

テスト実行中は以下でメトリクスを確認できます：

- **Prometheus**: http://localhost:9090
- **Grafana**: http://localhost:3000 (admin/admin)
- **メトリクス API**: http://localhost:8000/metrics

### Azure Monitor メトリクス収集

負荷試験実行時に、以下の Azure リソースのメトリクスを自動収集します：

#### 収集対象

- **Azure Functions**: 実行時間、スループット、エラー率、同時実行数、メモリ使用量
- **CosmosDB**: RU 消費量、スロットリング発生率、応答時間
- **IoT Hub**: 受信スループット、メッセージ遅延、接続デバイス数

#### 出力ファイル

- `results/metrics/azure_metrics_raw_*.csv` - 全メトリクスの時系列データ
- `results/metrics/azure_metrics_summary_*.csv` - ステップごとの統計サマリー
- `results/metrics/graphs_*/` - メトリクスのグラフ画像（PNG 形式）

詳細な使用方法は [Azure Metrics Guide](./docs/AZURE_METRICS_GUIDE.md) を参照してください。

## 結果ファイル

テスト結果は `./results/` ディレクトリに保存されます：

```
results/
├── device_scaling_20250903_143000.json    # 詳細メトリクス
├── device_scaling_20250903_143000.xlsx    # Excelレポート
└── device_scaling_20250903_summary.json   # サマリー
```

## 主要設定ファイル

- `configs/scenarios/device_scaling.json`: デバイス数スケーリング設定
- `configs/scenarios/message_frequency.json`: メッセージ頻度設定
- `configs/scenarios/data_size_load.json`: データサイズ負荷設定

## トラブルシューティング

### よくあるエラー

**接続エラー**:

```bash
# 接続文字列確認
echo $IOTHUB_CONNECTION_STRING
```

**デバイス認証エラー**:

```bash
# デバイス登録状況確認
python scripts/verify_devices.py --device-type bacnet --count 50
```

**メモリ不足**:

```bash
# デバイス数を削減して実行
python scripts/run_test.py --scenario device_scaling --step 1 --config-override '{"steps":[{"step_id":1,"devices_per_type":10}]}'
```

## 詳細情報

- 設計詳細: [DESIGN.md](./DESIGN.md)
- 使用方法: [docs/USAGE.md](./docs/USAGE.md)
- メトリクス仕様: [docs/METRICS.md](./docs/METRICS.md)
- Azure メトリクス収集: [docs/AZURE_METRICS_GUIDE.md](./docs/AZURE_METRICS_GUIDE.md)
