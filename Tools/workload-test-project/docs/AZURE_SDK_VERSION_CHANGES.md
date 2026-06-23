# Azure Monitor SDK バージョン変更メモ

## 変更内容

### 旧バージョン (azure-monitor-query < 2.0.0)
- `azure-monitor-query>=1.3.0`
- `MetricsQueryClient` を使用
- `MetricAggregationType` Enum を使用

```python
from azure.monitor.query import MetricsQueryClient, MetricAggregationType

client = MetricsQueryClient(credential)
response = client.query_resource(
    resource_uri=resource_id,
    metric_names=[metric_name],
    timespan=(start_time, end_time),
    granularity=timedelta(minutes=1),
    aggregations=[MetricAggregationType.AVERAGE]
)
```

### 新バージョン (azure-monitor-query >= 2.0.0)
- `azure-mgmt-monitor>=6.0.0` を使用
- `MonitorManagementClient` を使用
- 文字列で集約タイプを指定 ('Average', 'Total', 'Maximum', 'Minimum')

```python
from azure.mgmt.monitor import MonitorManagementClient

client = MonitorManagementClient(credential, subscription_id)
response = client.metrics.list(
    resource_uri=resource_id,
    timespan=f"{start_time.isoformat()}/{end_time.isoformat()}",
    interval='PT1M',
    metricnames=metric_name,
    aggregation='Average'
)
```

## 主な違い

1. **パッケージ名**
   - 旧: `azure-monitor-query`
   - 新: `azure-mgmt-monitor`

2. **クライアントクラス**
   - 旧: `MetricsQueryClient(credential)`
   - 新: `MonitorManagementClient(credential, subscription_id)`

3. **メソッド名**
   - 旧: `client.query_resource()`
   - 新: `client.metrics.list()`

4. **タイムスパン指定**
   - 旧: `timespan=(start_time, end_time)` (tuple)
   - 新: `timespan="start/end"` (ISO 8601 文字列)

5. **粒度指定**
   - 旧: `granularity=timedelta(minutes=1)` (timedelta)
   - 新: `interval='PT1M'` (ISO 8601 duration)

6. **集約タイプ**
   - 旧: `MetricAggregationType.AVERAGE` (Enum)
   - 新: `'Average'` (文字列)

7. **レスポンス構造**
   - 旧: `response.metrics`
   - 新: `response.value`

## 移行理由

Azure Monitor Query ライブラリ version 2.0.0 では、`MetricsQueryClient` が削除され、メトリクス機能が別のパッケージに分離されました。新しい `azure-mgmt-monitor` パッケージは、より安定した Azure Management SDK の一部として提供されています。

## 参考リンク

- [azure-mgmt-monitor PyPI](https://pypi.org/project/azure-mgmt-monitor/)
- [azure-monitor-query 2.0.0 Release Notes](https://github.com/Azure/azure-sdk-for-python/releases/tag/azure-monitor-query_2.0.0)

