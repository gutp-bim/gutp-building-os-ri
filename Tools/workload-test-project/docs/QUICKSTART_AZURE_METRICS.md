# Azure メトリクス収集 - クイックスタートガイド

## 5 分でできる！Azure メトリクス収集の開始

### ステップ 1: 依存パッケージのインストール

```bash
cd Tools/workload-test-project
pip install -r requirements.txt
```

**注意**: `azure-mgmt-monitor>=6.0.0` が必要です。`azure-monitor-query` 2.0.0 では API が変更されているため、`azure-mgmt-monitor` を使用しています。

### ステップ 2: Azure Service Principal の作成

```bash
# Azure CLI でログイン
az login

# サービスプリンシパル作成（以下を1行で実行）
az ad sp create-for-rbac \
  --name "building-os-loadtest-metrics" \
  --role "Monitoring Reader" \
  --scopes /subscriptions/YOUR_SUBSCRIPTION_ID

# 出力をメモ
# {
#   "appId": "xxx",        <- AZURE_CLIENT_ID
#   "password": "xxx",     <- AZURE_CLIENT_SECRET
#   "tenant": "xxx"        <- AZURE_TENANT_ID
# }
```

### ステップ 3: 環境変数の設定

`.env` ファイルを編集（または作成）：

```bash
# Azure 認証情報
AZURE_SUBSCRIPTION_ID="your-subscription-id-here"
AZURE_TENANT_ID="tenant-id-from-step2"
AZURE_CLIENT_ID="appId-from-step2"
AZURE_CLIENT_SECRET="password-from-step2"

# Azure Functions（あなたのリソース名に置き換え）
FUNCTION_RESOURCE_GROUP="your-rg-name"
FUNCTION_APP_NAME="your-function-name"

# CosmosDB（あなたのリソース名に置き換え）
COSMOSDB_RESOURCE_GROUP="your-rg-name"
COSMOSDB_ACCOUNT_NAME="your-cosmos-account"

# IoT Hub（あなたのリソース名に置き換え）
IOTHUB_RESOURCE_GROUP="your-rg-name"
IOTHUB_NAME="your-iothub-name"
```

### ステップ 4: テスト実行

```bash
# メトリクス収集を有効にして負荷試験を実行
python scripts/run_test.py \
  --scenario device_scaling \
  --step 1 \
  --duration 10

# または、サンプルスクリプトで動作確認
python scripts/sample_azure_metrics.py --mode once
```

### ステップ 5: 結果の確認

```bash
# 生成されたファイルを確認
ls -la results/metrics/

# CSV ファイルを開く
# - azure_metrics_raw_*.csv      <- 全データ
# - azure_metrics_summary_*.csv  <- 統計サマリー

# グラフを確認
# graphs_*/ ディレクトリ内の PNG ファイル
```

## 出力されるファイル

### 1. Raw データ CSV

全メトリクスの時系列データ：

| timestamp           | resource_type | resource_name | metric_name                  | value | unit | step_id | step_name |
| ------------------- | ------------- | ------------- | ---------------------------- | ----- | ---- | ------- | --------- |
| 2025-11-08T10:00:00 | function      | my-func       | function_execution_time      | 235.5 | ms   | 1       | Step 1    |
| 2025-11-08T10:01:00 | cosmosdb      | my-db         | cosmosdb_total_request_units | 1250  | RU   | 1       | Step 1    |

### 2. サマリー CSV

ステップごとの統計：

| step_id | step_name | resource_type | metric_name             | avg   | min | max | std  | count |
| ------- | --------- | ------------- | ----------------------- | ----- | --- | --- | ---- | ----- |
| 1       | Step 1    | function      | function_execution_time | 240.3 | 210 | 280 | 15.2 | 60    |

### 3. グラフ（PNG）

- **時系列グラフ**: メトリクスの時間推移
- **ステップ別比較グラフ**: ステップごとの平均値比較

## トラブルシューティング

### ❌ "AZURE_SUBSCRIPTION_ID not set" エラー

**解決方法**: `.env` ファイルに環境変数を設定してください。

```bash
# 環境変数を確認
cat .env | grep AZURE_SUBSCRIPTION_ID
```

### ❌ 認証エラー "AuthenticationError"

**解決方法**: Service Principal の認証情報を確認してください。

```bash
# サービスプリンシパルの確認
az ad sp list --display-name "building-os-loadtest-metrics"

# ロール割り当ての確認
az role assignment list --assignee YOUR_CLIENT_ID
```

### ❌ "No Azure resources configured"

**解決方法**: リソースグループ名とリソース名が正しいか確認してください。

```bash
# Azure Functions の確認
az functionapp list --query "[].{name:name,resourceGroup:resourceGroup}" -o table

# CosmosDB の確認
az cosmosdb list --query "[].{name:name,resourceGroup:resourceGroup}" -o table

# IoT Hub の確認
az iot hub list --query "[].{name:name,resourceGroup:resourceGroup}" -o table
```

### ⚠️ メトリクスデータが少ない

**原因**: 収集期間中にリソースが動作していない可能性があります。

**解決方法**:

1. 負荷試験を実行しながらメトリクス収集
2. 収集間隔を短くする（デフォルト 60 秒 → 30 秒など）

## Excel での分析例

### 1. CSV を Excel で開く

```
azure_metrics_summary_*.csv を Excel で開く
```

### 2. ピボットテーブルを作成

- **行**: step_name
- **列**: metric_name
- **値**: avg（平均）

### 3. グラフを挿入

- 折れ線グラフまたは棒グラフを選択
- ステップごとのメトリクス推移を可視化

## Python での分析例

```python
import pandas as pd
import matplotlib.pyplot as plt

# データ読み込み
df = pd.read_csv('results/metrics/azure_metrics_raw_*.csv')

# 特定メトリクスをフィルタ
function_metrics = df[
    (df['resource_type'] == 'function') &
    (df['metric_name'] == 'function_execution_time')
]

# グラフ作成
plt.figure(figsize=(12, 6))
plt.plot(pd.to_datetime(function_metrics['timestamp']),
         function_metrics['value'])
plt.xlabel('Time')
plt.ylabel('Execution Time (ms)')
plt.title('Azure Functions Execution Time')
plt.xticks(rotation=45)
plt.tight_layout()
plt.savefig('function_execution_time.png')
plt.show()
```

## より詳しい情報

- [Azure Metrics Guide](./AZURE_METRICS_GUIDE.md) - 完全なドキュメント
- [README.md](../README.md) - プロジェクト全体のドキュメント

## サポートが必要な場合

1. まず [Azure Metrics Guide](./AZURE_METRICS_GUIDE.md) のトラブルシューティングセクションを確認
2. ログファイル（`logs/` ディレクトリ）を確認
3. Azure Portal でリソースが正常に動作しているか確認
