# メトリクス仕様

## 収集対象メトリクス

### 1. 負荷試験システム内部メトリクス

#### 1.1 メッセージ送信メトリクス

| メトリクス名 | 型 | ラベル | 説明 |
|-------------|----|---------|----- |
| `loadtest_messages_sent_total` | Counter | `device_type`, `scenario` | 送信成功メッセージ総数 |
| `loadtest_messages_failed_total` | Counter | `device_type`, `scenario`, `error_type` | 送信失敗メッセージ総数 |
| `loadtest_message_send_duration_seconds` | Histogram | `device_type` | メッセージ送信時間分布 |
| `loadtest_device_connection_duration_seconds` | Histogram | `device_type` | デバイス接続時間分布 |

#### 1.2 デバイス状態メトリクス

| メトリクス名 | 型 | ラベル | 説明 |
|-------------|----|---------|----- |
| `loadtest_active_devices` | Gauge | `device_type`, `scenario` | アクティブデバイス数 |
| `loadtest_connected_devices` | Gauge | `device_type` | 接続済みデバイス数 |
| `loadtest_failed_devices` | Gauge | `device_type` | 失敗デバイス数 |
| `loadtest_device_error_rate` | Gauge | `device_type` | デバイス別エラー率 |

#### 1.3 スループット・パフォーマンス

| メトリクス名 | 型 | ラベル | 説明 |
|-------------|----|---------|----- |
| `loadtest_throughput_messages_per_second` | Gauge | `device_type`, `scenario` | スループット（メッセージ/秒） |
| `loadtest_total_data_size_bytes` | Counter | `device_type` | 送信データ総量 |
| `loadtest_average_message_size_bytes` | Gauge | `device_type` | 平均メッセージサイズ |

### 2. Azure 関連メトリクス（参考用）

#### 2.1 Azure IoT Hub

| メトリクス | 説明 | 収集方法 |
|-----------|------|----------|
| `d2c.telemetry.ingress.allMessages` | 受信メッセージ総数 | Azure Monitor API |
| `d2c.telemetry.egress.success` | 正常転送メッセージ数 | Azure Monitor API |
| `d2c.telemetry.egress.dropped` | ドロップメッセージ数 | Azure Monitor API |
| `devices.totalDevices` | 登録デバイス総数 | Azure Monitor API |
| `devices.connectedDevices.allProtocols` | 接続デバイス数 | Azure Monitor API |

#### 2.2 Azure Functions

| メトリクス | 説明 | 収集方法 |
|-----------|------|----------|
| `FunctionExecutionCount` | Function実行回数 | Application Insights |
| `FunctionExecutionUnits` | 実行ユニット数 | Application Insights |
| `MemoryWorkingSet` | メモリ使用量 | Application Insights |
| `Exceptions` | 例外発生数 | Application Insights |

#### 2.3 CosmosDB

| メトリクス | 説明 | 収集方法 |
|-----------|------|----------|
| `TotalRequestUnits` | RU消費量 | Azure Monitor API |
| `UserErrors` | ユーザーエラー数 | Azure Monitor API |
| `ThrottledRequests` | スロットリング発生数 | Azure Monitor API |
| `ServerSideLatency` | サーバー側レイテンシ | Azure Monitor API |

## メトリクス出力形式

### 1. Prometheus 形式

```text
# HELP loadtest_messages_sent_total Total messages sent
# TYPE loadtest_messages_sent_total counter
loadtest_messages_sent_total{device_type="bacnet",scenario="device_scaling"} 15000.0

# HELP loadtest_message_send_duration_seconds Message send duration
# TYPE loadtest_message_send_duration_seconds histogram
loadtest_message_send_duration_seconds_bucket{device_type="bacnet",le="0.1"} 12000.0
loadtest_message_send_duration_seconds_bucket{device_type="bacnet",le="0.5"} 14800.0
loadtest_message_send_duration_seconds_bucket{device_type="bacnet",le="1.0"} 15000.0
loadtest_message_send_duration_seconds_sum{device_type="bacnet"} 2350.5
loadtest_message_send_duration_seconds_count{device_type="bacnet"} 15000.0
```

### 2. JSON レポート形式

```json
{
  "test_summary": {
    "scenario": "device_scaling",
    "start_time": "2025-09-03T14:30:00+09:00",
    "end_time": "2025-09-03T16:30:00+09:00", 
    "total_duration_minutes": 120,
    "steps_executed": [1, 2, 3, 4]
  },
  "step_results": {
    "1": {
      "step_name": "初期負荷（250台）",
      "devices_per_type": 50,
      "total_devices": 250,
      "duration_minutes": 30,
      "metrics": {
        "messages_sent": 15000,
        "messages_failed": 12,
        "error_rate_percent": 0.08,
        "average_send_time_ms": 156.7,
        "throughput_msgs_per_sec": 8.33,
        "max_response_time_ms": 2450
      },
      "device_type_breakdown": {
        "bacnet": {
          "devices": 50,
          "messages": 3000,
          "errors": 2,
          "avg_send_time_ms": 142.3
        },
        "hvac": {
          "devices": 50,
          "messages": 3000,
          "errors": 1,
          "avg_send_time_ms": 167.8
        }
      }
    }
  },
  "overall_results": {
    "max_stable_devices": 1000,
    "max_throughput_msgs_per_sec": 25.6,
    "bottleneck_component": "CosmosDB",
    "recommended_scaling": {
      "max_devices_per_instance": 500,
      "recommended_message_interval": 30
    }
  }
}
```

## エラー分類

### 1. 接続エラー

| エラータイプ | 説明 | 対処方法 |
|-------------|------|----------|
| `connection_timeout` | IoT Hub接続タイムアウト | 接続文字列・ネットワーク確認 |
| `authentication_failed` | 認証失敗 | デバイス登録・SASキー確認 |
| `connection_refused` | 接続拒否 | IoT Hub の接続制限確認 |

### 2. メッセージ送信エラー

| エラータイプ | 説明 | 対処方法 |
|-------------|------|----------|
| `send_timeout` | 送信タイムアウト | ネットワーク・メッセージサイズ確認 |
| `quota_exceeded` | クォータ超過 | IoT Hub の日次メッセージ制限確認 |
| `throttling` | スロットリング | 送信間隔を調整 |
| `invalid_message` | メッセージ形式エラー | テンプレート・JSON形式確認 |

### 3. システムエラー

| エラータイプ | 説明 | 対処方法 |
|-------------|------|----------|
| `memory_exceeded` | メモリ不足 | デバイス数削減・リソース増加 |
| `cpu_overload` | CPU使用率過大 | 並列度削減・処理分散 |
| `network_congestion` | ネットワーク輻輳 | 送信間隔調整・帯域確認 |

## パフォーマンス評価基準

### 1. 成功基準

| 指標 | 目標値 | 備考 |
|------|--------|------|
| エラー率 | < 5% | 一時的なネットワークエラーを除く |
| 平均応答時間 | < 3秒 | メッセージ送信からACK受信まで |
| スループット | > 10 msg/sec/device | デバイスあたりの処理能力 |
| 接続安定性 | > 95% | 30分間の接続維持率 |

### 2. 警告しきい値

| 指標 | 警告レベル | 対応 |
|------|----------|------|
| エラー率 | > 3% | ログ詳細確認・原因調査 |
| 応答時間 | > 5秒 | システムリソース確認 |
| メモリ使用率 | > 80% | デバイス数削減検討 |
| CPU使用率 | > 90% | 並列度削減 |

### 3. ボトルネック特定指標

#### Azure Functions
```text
実行時間 > 10秒 → Function timeout設定確認
同時実行数 > 200 → 水平スケーリング検討
例外率 > 1% → コード・設定見直し
```

#### CosmosDB
```text
RU使用率 > 80% → パーティション設計・RU増強検討
スロットリング > 0% → クエリ最適化・RU調整
応答時間 > 100ms → インデックス・パーティション確認
```

## カスタムメトリクス追加

### 1. 新規メトリクス定義

```python
# src/core/metrics_collector.py に追加
self.custom_metric = Gauge(
    'loadtest_custom_metric_value',
    'Custom metric description',
    ['device_type', 'custom_label']
)
```

### 2. メトリクス記録

```python
# デバイスクラス内で記録
self.metrics_collector.custom_metric.labels(
    device_type=self.device_type,
    custom_label="test_value"
).set(measurement_value)
```

### 3. レポートに含める

```python
# scripts/generate_report.py で集計処理追加
custom_metrics = self.collect_custom_metrics()
report_data["custom_analysis"] = custom_metrics
```

## 監視ダッシュボード

### Grafana クエリ例

#### メッセージスループット
```promql
rate(loadtest_messages_sent_total[5m])
```

#### エラー率
```promql
rate(loadtest_messages_failed_total[5m]) / rate(loadtest_messages_sent_total[5m]) * 100
```

#### レスポンス時間 99パーセンタイル
```promql
histogram_quantile(0.99, rate(loadtest_message_send_duration_seconds_bucket[5m]))
```

#### アクティブデバイス数推移
```promql
loadtest_active_devices
```