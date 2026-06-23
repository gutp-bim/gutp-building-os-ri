# 共有接続設計

## 概要

このドキュメントでは、複数のデバイスインスタンスで1つのIoT Hub接続を共有する設計について説明します。

## 背景

### 問題

従来の設計では、各シナリオがconfigで指定した台数分の`BaseDevice`インスタンスを作成し、それぞれが独立した`IoTHubDeviceClient`を作成してIoT Hubに接続していました。しかし、ローカル環境で実行すると、2個以上のDeviceインスタンスを作成した場合、IoT Hubとの接続が2個目以降正常に確立できないという問題が発生しました。

### 解決策

1つのIoT Hub接続を複数のデバイスIDで共有する設計に変更しました。実際には1つの物理的な接続のみを確立し、メッセージペイロードで複数のデバイスをエミュレートします。

## アーキテクチャ

### 主要なコンポーネント

#### 1. SharedConnectionDevice

**役割**: 単一のIoT Hub接続を管理し、複数の仮想デバイスで共有する

**特徴**:
- 1つの`IoTHubDeviceClient`インスタンスのみを保持
- 接続は1回のみ確立される（`_connection_lock`でスレッドセーフ）
- 各仮想デバイスIDごとに統計情報（メッセージ数、エラー数など）を記録
- メッセージに`__emulated_device_id`フィールドを追加して、送信元デバイスを識別

#### 2. VirtualDevice

**役割**: 共有接続を使用する仮想デバイス

**特徴**:
- 実際の接続は持たない
- `SharedConnectionDevice`を介してメッセージを送信
- 既存の`BaseDevice`と互換性のあるインターフェース（`send_message`, `run_continuous`など）
- メッセージ生成関数を外部から設定可能

#### 3. DeviceFactory（拡張）

**新規メソッド**:
- `create_shared_connection()`: 共有接続インスタンスを作成
- `create_virtual_devices_batch()`: 共有接続を使用する仮想デバイスを一括作成

### 接続の流れ

```
[シナリオ] 
    └─> [DeviceFactory.create_shared_connection()] 
            └─> [SharedConnectionDevice] (IoT Hubに1回だけ接続)
                    ├─> [VirtualDevice 1] (接続なし、SharedConnectionDeviceを参照)
                    ├─> [VirtualDevice 2] (接続なし、SharedConnectionDeviceを参照)
                    └─> [VirtualDevice N] (接続なし、SharedConnectionDeviceを参照)
```

### メッセージ送信の流れ

```
[VirtualDevice 1] 
    └─> send_message() 
            └─> [SharedConnectionDevice.send_message(device_id="device1", ...)]
                    └─> メッセージに __emulated_device_id を追加
                    └─> IoT Hubに送信（1つの接続を使用）

[VirtualDevice 2]
    └─> send_message()
            └─> [SharedConnectionDevice.send_message(device_id="device2", ...)]
                    └─> メッセージに __emulated_device_id を追加
                    └─> IoT Hubに送信（同じ1つの接続を使用）
```

## シナリオの変更点

### 1. DataSizeLoadScenario

**変更前**:
```python
self.active_devices: List[BaseDevice] = []
# 各デバイスが個別に接続
devices = self.device_factory.create_devices_batch(...)
self.active_devices.extend(devices)
```

**変更後**:
```python
self.shared_connections: Dict[str, SharedConnectionDevice] = {}
self.virtual_devices: List[VirtualDevice] = []

# デバイスタイプごとに1つの共有接続を作成
shared_connection = self.device_factory.create_shared_connection(...)
await shared_connection.connect()  # 1回だけ接続

# 共有接続を使用する仮想デバイスを作成
virtual_devices = self.device_factory.create_virtual_devices_batch(
    shared_connection=shared_connection, ...
)
```

### 2. MessageFrequencyScenario

DataSizeLoadScenarioと同様の変更を適用

### 3. DeviceScalingScenario

**特徴**:
- 各ステップで新しい仮想デバイスを追加する際、既存の共有接続を再利用
- デバイスタイプごとに1つの共有接続を維持

```python
# 共有接続がまだない場合は作成
if device_type not in self.shared_connections:
    shared_connection = self.device_factory.create_shared_connection(...)
    await shared_connection.connect()
    self.shared_connections[device_type] = shared_connection

# 既存の共有接続を使用
shared_connection = self.shared_connections[device_type]
virtual_devices = self.device_factory.create_virtual_devices_batch(
    shared_connection=shared_connection, ...
)
```

## 利点

1. **接続の安定性**: 複数の物理的な接続を確立する必要がなくなり、接続エラーが大幅に減少
2. **リソース効率**: 1つの接続のみを維持するため、ネットワークリソースとメモリ使用量が削減
3. **スケーラビリティ**: 数百、数千の仮想デバイスをエミュレートできる
4. **互換性**: 既存の`BaseDevice`インターフェースとの互換性を維持

## 制約事項

1. **認証**: すべての仮想デバイスは同じプライマリデバイスの認証情報を使用
2. **デバイスツイン**: 各仮想デバイスが独立したデバイスツインを持つことはできない
3. **C2Dメッセージ**: Cloud-to-Device メッセージは、プライマリデバイスIDに対してのみ受信可能
4. **ダイレクトメソッド**: ダイレクトメソッドは、プライマリデバイスIDに対してのみ呼び出し可能

## 使用方法

### 基本的な使い方

```python
# 1. 共有接続を作成
shared_connection = device_factory.create_shared_connection(
    device_type="HVAC",
    primary_device_id="test-hvac-primary"
)

# 2. 接続を確立
await shared_connection.connect()

# 3. 仮想デバイスを作成
virtual_devices = device_factory.create_virtual_devices_batch(
    device_type="HVAC",
    count=10,
    shared_connection=shared_connection,
    id_prefix="test-hvac"
)

# 4. メッセージ送信
for device in virtual_devices:
    await device.send_message(point_count=10)

# 5. クリーンアップ
await shared_connection.disconnect()
```

### シナリオでの使用

既存のシナリオは自動的に共有接続を使用するように更新されています。使用方法は変わりません。

```python
# 既存のコード（変更不要）
scenario = DataSizeLoadScenario(config, device_factory, metrics_collector)
result = await scenario.execute_step(step_id=1)
```

## 統計情報

共有接続は、デバイスIDごとに統計情報を記録します:

```python
# 特定のデバイスの統計
stats = shared_connection.get_stats(device_id="test-hvac-0001")
print(f"Messages sent: {stats['messages_sent']}")
print(f"Errors: {stats['errors']}")
print(f"Average send time: {stats['average_send_time']}")

# 全体の統計
overall_stats = shared_connection.get_stats()
print(f"Total messages: {overall_stats['total_messages_sent']}")
print(f"Emulated device count: {overall_stats['emulated_device_count']}")
```

## トラブルシューティング

### 接続エラー

**問題**: 共有接続の確立に失敗する

**解決策**:
1. 接続文字列が正しいか確認
2. ネットワーク接続を確認
3. IoT Hubのデバイスが有効化されているか確認

### メッセージ送信エラー

**問題**: メッセージ送信が失敗する

**解決策**:
1. 共有接続が確立されているか確認（`shared_connection.is_connected`）
2. IoT Hubのクォータ制限を確認
3. メッセージサイズがIoT Hubの制限内か確認

## まとめ

共有接続設計により、複数のデバイスインスタンスを使用する際の接続問題が解決され、よりスケーラブルで安定したテスト環境が実現されました。既存のコードとの互換性も維持されており、シームレスな移行が可能です。

