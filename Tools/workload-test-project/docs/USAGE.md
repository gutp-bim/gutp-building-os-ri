# 負荷試験システム 使用方法

## 基本的な実行フロー

### 1. 事前準備

#### Azure IoT Hub デバイス登録
```bash
# 最大デバイス数まで一括登録（初回のみ）
python scripts/setup_devices.py --scenario device_scaling --max-devices 1000
python scripts/setup_devices.py --scenario message_frequency --max-devices 1000
python scripts/setup_devices.py --scenario data_size_load --max-devices 500
```

#### 接続確認
```bash
# 接続テスト（少数デバイスで確認）
python scripts/run_test.py --scenario device_scaling --step 1 --duration 1 --dry-run
```

### 2. シナリオ別実行方法

#### シナリオ1: デバイス数スケーリング試験

```bash
# 段階的実行（推奨）
python scripts/run_test.py --scenario device_scaling --step 1 --debug
# 結果確認後、次ステップ
python scripts/run_test.py --scenario device_scaling --step 2
python scripts/run_test.py --scenario device_scaling --step 3
# 問題なければ最大負荷
python scripts/run_test.py --scenario device_scaling --step 4

# 一括実行（自動化時）
python scripts/run_test.py --scenario device_scaling --step all
```

#### シナリオ2: メッセージ頻度ストレス試験

```bash
# 1000台固定でメッセージ間隔を段階的に短縮
python scripts/run_test.py --scenario message_frequency --step all

# 特定間隔のみテスト
python scripts/run_test.py --scenario message_frequency --step 3,4  # 15秒・5秒間隔
```

#### シナリオ3: データサイズ負荷試験

```bash
# 500台固定でメッセージサイズを段階的に拡大
python scripts/run_test.py --scenario data_size_load --step all

# 大容量メッセージのみテスト
python scripts/run_test.py --scenario data_size_load --step 3,4  # 50・100ポイント
```

## 詳細オプション

### デバイスタイプ指定

```bash
# 特定デバイスタイプのみでテスト
python scripts/run_test.py --scenario device_scaling --step 1,2 --device-types bacnet,hvac

# 単一デバイスタイプでの最大負荷確認
python scripts/run_test.py --scenario device_scaling --step all --device-types bacnet
```

### 実行時間調整

```bash
# クイックテスト（5分間）
python scripts/run_test.py --scenario device_scaling --step 1,2 --duration 5

# 詳細テスト（60分間）
python scripts/run_test.py --scenario device_scaling --step 3 --duration 60
```

### 設定カスタマイズ

```bash
# カスタム設定ファイル使用
cp configs/scenarios/device_scaling.json configs/custom/my_test.json
# my_test.json を編集後
python scripts/run_test.py --scenario device_scaling --config configs/custom/my_test.json

# 実行時設定オーバーライド
python scripts/run_test.py --scenario device_scaling --step 1 \
  --config-override '{"steps":[{"step_id":1,"devices_per_type":25,"duration_minutes":10}]}'
```

## Docker実行

### 基本実行

```bash
# Docker Compose での実行
docker-compose up

# バックグラウンド実行
docker-compose up -d

# ログ確認
docker-compose logs -f loadtest
```

### 並列実行

```bash
# 複数コンテナでの並列実行
docker-compose up --scale loadtest=3

# デバイスタイプ別並列実行
docker-compose up loadtest-bacnet loadtest-hvac loadtest-environmental
```

## 監視とメトリクス

### リアルタイム監視

```bash
# メトリクス確認（テスト実行中）
curl http://localhost:8000/metrics

# Prometheus UI
open http://localhost:9090

# Grafana ダッシュボード
open http://localhost:3000  # admin/admin
```

### ログ監視

```bash
# テスト実行ログのリアルタイム表示
tail -f logs/loadtest_$(date +%Y%m%d).log

# エラーのみ表示
tail -f logs/loadtest_$(date +%Y%m%d).log | grep ERROR
```

## 実際の試験実施手順

### 事前チェックリスト

- [ ] Azure IoT Hub の準備確認
- [ ] デバイス登録の完了確認
- [ ] 接続文字列の動作確認
- [ ] ドライランでの動作確認
- [ ] メトリクス監視環境の準備

### 推奨実施順序

#### 1. 接続確認（5分）
```bash
python scripts/run_test.py --scenario device_scaling --step 1 --duration 1 --device-types bacnet --debug
```

#### 2. 小規模負荷テスト（30分）
```bash
python scripts/run_test.py --scenario device_scaling --step 1,2 --duration 15
```

#### 3. 段階的負荷増加（2-3時間）
```bash
python scripts/run_test.py --scenario device_scaling --step all
```

#### 4. 頻度ストレステスト（2時間）
```bash
python scripts/run_test.py --scenario message_frequency --step all
```

#### 5. データサイズテスト（2時間）
```bash
python scripts/run_test.py --scenario data_size_load --step all
```

## 結果の確認方法

### 即座の結果確認

```bash
# 最新のテスト結果表示
python scripts/show_latest_result.py

# 特定テストの結果表示
python scripts/show_result.py results/device_scaling_20250903_143000.json
```

### Excel レポート生成

```bash
# Excel レポート作成
python scripts/generate_excel_report.py \
  --input results/device_scaling_20250903_143000.json \
  --output reports/device_scaling_report.xlsx
```

### 比較レポート

```bash
# 複数回実行結果の比較
python scripts/compare_results.py \
  results/device_scaling_20250901_*.json \
  --output reports/scaling_comparison.xlsx
```

## 緊急停止・復旧

### 異常時の緊急停止

```bash
# 全テスト強制停止
docker-compose down
pkill -f "python.*run_test"

# デバイス接続全切断
python scripts/emergency_disconnect.py
```

### 部分的な問題対処

```bash
# 特定デバイスタイプの切断
python scripts/disconnect_devices.py --device-type bacnet

# エラーデバイスのみ再接続
python scripts/reconnect_failed_devices.py --scenario device_scaling
```

## パフォーマンスチューニング

### リソース制限

```bash
# メモリ使用量を制限して実行
docker run --memory=2g --cpus=1.5 building-os-loadtest \
  --scenario device_scaling --step 1,2
```

### 並列度調整

```bash
# 接続処理の並列度を調整
export MAX_CONCURRENT_CONNECTIONS=50
python scripts/run_test.py --scenario device_scaling --step 1
```

## 設定例

### カスタムしきい値設定

```json
{
  "thresholds": {
    "error_rate_percent": 3.0,
    "timeout_seconds": 15,
    "max_response_time_ms": 2000
  }
}
```

### デバッグ用設定

```json
{
  "steps": [
    {
      "step_id": 1,
      "step_name": "デバッグ用小規模テスト",
      "devices_per_type": 5,
      "duration_minutes": 2
    }
  ]
}
```

## 注意事項

- **本番環境影響**: 本番用Consumer Groupを使用しないよう注意
- **コスト管理**: 大量メッセージ送信によるAzure利用料金に注意
- **リソース制限**: ローカルマシンのメモリ・CPU使用量を監視
- **並行実行**: 複数の負荷テストを同時実行する場合はリソース競合に注意