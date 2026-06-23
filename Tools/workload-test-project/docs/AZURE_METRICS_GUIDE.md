# Azure Monitor メトリクス収集ガイド

## 概要

Building OS 負荷試験システムでは、Azure Monitor API を使用して以下のメトリクスを自動的に収集します：

### 収集対象メトリクス

#### 1. Azure Functions (Connector)

- **実行時間**: 1 回あたりの平均処理時間
- **スループット**: 1 分間あたり処理メッセージ数
- **エラー率**: タイムアウト・例外発生率
- **同時実行数**: アクティブインスタンス数
- **メモリ使用量**: 関数実行時のメモリ消費

#### 2. CosmosDB

- **RU 消費量**: リクエストユニット使用率
- **スロットリング発生率**: リクエスト制限発生頻度
- **応答時間**: クエリ実行時間（サーバー側レイテンシー）
- **平均 RU/リクエスト**: リクエストあたりの平均 RU 消費量

#### 3. Azure IoT Hub

- **受信スループット**: 受信イベント数/秒
- **メッセージ遅延**: エンドツーエンド処理時間（メッセージ受信からイベントハブへの配信まで）
- **接続デバイス数**: 現在接続中のデバイス数
- **総デバイス数**: 登録されている総デバイス数

## セットアップ

### 1. 環境変数の設定

`.env` ファイルに以下の環境変数を追加してください：

```bash
# Azure サブスクリプション ID
AZURE_SUBSCRIPTION_ID="your-subscription-id"

# Azure 認証情報 (Service Principal)
AZURE_TENANT_ID="your-tenant-id"
AZURE_CLIENT_ID="your-client-id"
AZURE_CLIENT_SECRET="your-client-secret"

# Azure Functions
FUNCTION_RESOURCE_GROUP="your-function-resource-group"
FUNCTION_APP_NAME="your-function-app-name"

# Azure CosmosDB
COSMOSDB_RESOURCE_GROUP="your-cosmosdb-resource-group"
COSMOSDB_ACCOUNT_NAME="your-cosmosdb-account"
COSMOSDB_DATABASE_NAME="your-database-name"

# Azure IoT Hub
IOTHUB_RESOURCE_GROUP="your-iothub-resource-group"
IOTHUB_NAME="your-iothub-name"
```

### 2. Azure Service Principal の作成

Azure CLI を使用して Service Principal を作成します：

```bash
# サービスプリンシパルを作成
az ad sp create-for-rbac --name "loadtest-metrics-collector" \
  --role "Monitoring Reader" \
  --scopes /subscriptions/YOUR_SUBSCRIPTION_ID

# 出力された情報を環境変数に設定
# appId -> AZURE_CLIENT_ID
# password -> AZURE_CLIENT_SECRET
# tenant -> AZURE_TENANT_ID
```

### 3. 必要な権限の付与

Service Principal に以下のロールを付与：

```bash
# Monitoring Reader ロールを付与
az role assignment create \
  --assignee YOUR_CLIENT_ID \
  --role "Monitoring Reader" \
  --scope /subscriptions/YOUR_SUBSCRIPTION_ID
```

## 使用方法

### 基本的な使用

メトリクス収集は負荷試験実行時に自動的に開始されます：

```bash
# 通常の負荷試験実行（メトリクス収集有効）
python scripts/run_test.py --scenario device_scaling --step all

# Azure メトリクス収集を無効化する場合
python scripts/run_test.py --scenario device_scaling --step all --no-azure-metrics
```

### メトリクス収集間隔の変更

デフォルトの収集間隔は 60 秒ですが、設定ファイルで変更可能です：

```json
// configs/azure_metrics_config.json
{
  "collection": {
    "interval_seconds": 30, // 30秒間隔に変更
    "enabled": true
  }
}
```

## 出力ファイル

テスト終了後、以下のファイルが `results/metrics/` ディレクトリに生成されます：

### 1. Raw データ (CSV)

`azure_metrics_raw_YYYYMMDD_HHMMSS.csv`

全メトリクスの時系列データを含む CSV ファイル。各行には以下の情報が含まれます：

| カラム        | 説明                                      |
| ------------- | ----------------------------------------- |
| timestamp     | メトリクス収集時刻                        |
| resource_type | リソースタイプ (function/cosmosdb/iothub) |
| resource_name | リソース名                                |
| metric_name   | メトリクス名                              |
| value         | メトリクス値                              |
| unit          | 単位                                      |
| step_id       | テストステップ ID                         |
| step_name     | テストステップ名                          |

### 2. サマリー (CSV)

`azure_metrics_summary_YYYYMMDD_HHMMSS.csv`

ステップごとの統計情報を含む CSV ファイル：

| カラム        | 説明              |
| ------------- | ----------------- |
| step_id       | テストステップ ID |
| step_name     | テストステップ名  |
| resource_type | リソースタイプ    |
| metric_name   | メトリクス名      |
| avg           | 平均値            |
| min           | 最小値            |
| max           | 最大値            |
| std           | 標準偏差          |
| count         | データポイント数  |

### 3. グラフ (PNG)

`graphs_YYYYMMDD_HHMMSS/` ディレクトリ内に生成されます：

- **時系列グラフ**: `{resource_type}_{metric_name}_timeseries.png`

  - 各メトリクスの時系列推移
  - ステップ境界が赤い点線で表示

- **ステップ別比較グラフ**: `{resource_type}_{metric_name}_by_step.png`
  - ステップごとの平均値を棒グラフで表示

## サンプル出力例

### Azure Functions メトリクス

```csv
timestamp,resource_type,resource_name,metric_name,value,unit,step_id,step_name
2025-11-08T10:00:00,function,my-connector-function,function_execution_time,235.5,ms,1,初期負荷（250台）
2025-11-08T10:01:00,function,my-connector-function,function_execution_time,242.1,ms,1,初期負荷（250台）
2025-11-08T10:02:00,function,my-connector-function,function_execution_count,180,count,1,初期負荷（250台）
```

### CosmosDB メトリクス

```csv
timestamp,resource_type,resource_name,metric_name,value,unit,step_id,step_name
2025-11-08T10:00:00,cosmosdb,my-cosmosdb,cosmosdb_total_request_units,1250.5,RU,1,初期負荷（250台）
2025-11-08T10:01:00,cosmosdb,my-cosmosdb,cosmosdb_server_latency,12.3,ms,1,初期負荷（250台）
```

## データ分析例

### Excel でのグラフ作成

1. `azure_metrics_summary_*.csv` を Excel で開く
2. ピボットテーブルを作成
3. 行：`step_name`、列：`metric_name`、値：`avg`
4. 折れ線グラフまたは棒グラフを挿入

### Python での分析

```python
import pandas as pd
import matplotlib.pyplot as plt

# Raw データを読み込み
df = pd.read_csv('results/metrics/azure_metrics_raw_20251108_100000.csv')

# IoT Hub の受信スループットを可視化
iothub_df = df[(df['resource_type'] == 'iothub') &
               (df['metric_name'] == 'iothub_throughput')]

plt.figure(figsize=(12, 6))
plt.plot(pd.to_datetime(iothub_df['timestamp']),
         iothub_df['value'])
plt.xlabel('Time')
plt.ylabel('Throughput (messages/sec)')
plt.title('IoT Hub Throughput Over Time')
plt.xticks(rotation=45)
plt.tight_layout()
plt.savefig('iothub_throughput.png')
```

## トラブルシューティング

### メトリクス収集が開始されない

**症状**: テスト実行時に「Azure metrics collection disabled」と表示される

**対策**:

1. 環境変数が正しく設定されているか確認

   ```bash
   echo $AZURE_SUBSCRIPTION_ID
   echo $AZURE_CLIENT_ID
   ```

2. Service Principal の権限を確認
   ```bash
   az role assignment list --assignee YOUR_CLIENT_ID
   ```

### 認証エラー

**症状**: `Failed to initialize Azure metrics collector: AuthenticationError`

**対策**:

1. Service Principal の認証情報を確認
2. テナント ID が正しいか確認
3. クライアントシークレットが有効期限内か確認

### メトリクスデータが取得できない

**症状**: CSV ファイルが空または一部のメトリクスのみ

**対策**:

1. リソース ID が正しいか確認
2. 対象リソースが存在し、動作しているか確認
3. メトリクス名が正しいか確認（Azure Portal で確認可能）
4. 収集期間中にデータが生成されているか確認

### グラフが生成されない

**症状**: グラフディレクトリが空

**対策**:

1. matplotlib, seaborn がインストールされているか確認

   ```bash
   pip install matplotlib seaborn
   ```

2. 日本語フォントがインストールされているか確認（Windows の場合は通常不要）

## 高度な使用方法

### カスタムメトリクスの追加

`src/core/azure_metrics_collector.py` を編集してカスタムメトリクスを追加：

```python
CUSTOM_METRICS = [
    MetricDefinition(
        name="custom_metric",
        display_name="カスタムメトリクス",
        metric_name="CustomMetricName",  # Azure Monitor でのメトリクス名
        aggregation=MetricAggregationType.AVERAGE,
        unit="count"
    )
]
```

### プログラムからの使用

```python
from src.core.azure_metrics_collector import AzureMetricsCollector
from datetime import datetime, timedelta, timezone

# 初期化
collector = AzureMetricsCollector(
    subscription_id="your-subscription-id",
    function_resource_ids=["your-function-resource-id"],
    use_environment_credential=True
)

# 指定期間のメトリクスを収集
end_time = datetime.now(timezone.utc)
start_time = end_time - timedelta(hours=1)

metrics = await collector.collect_metrics_once(
    start_time=start_time,
    end_time=end_time,
    step_id=1,
    step_name="Test Step"
)

# CSV にエクスポート
collector.export_to_csv("output/metrics.csv")
collector.export_summary_to_csv("output/summary.csv")
collector.generate_graphs("output/graphs")
```

**注意**: Azure SDK のバージョンについて

- このコードは `azure-mgmt-monitor>=6.0.0` を使用しています
- `azure-monitor-query` 2.0.0 では `MetricsQueryClient` が削除されたため、`MonitorManagementClient` を使用しています

## 参考資料

- [Azure Monitor Metrics API](https://docs.microsoft.com/ja-jp/azure/azure-monitor/essentials/metrics-supported)
- [Azure Functions Metrics](https://docs.microsoft.com/ja-jp/azure/azure-functions/functions-monitoring)
- [Azure Cosmos DB Metrics](https://docs.microsoft.com/ja-jp/azure/cosmos-db/monitor-cosmos-db)
- [Azure IoT Hub Metrics](https://docs.microsoft.com/ja-jp/azure/iot-hub/monitor-iot-hub)
