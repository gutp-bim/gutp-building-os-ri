# Building OS 負荷試験システム 設計書

## 1. 概要

Building OS の負荷試験計画書（`docs/test/workload-test/workload-test-plan.md`）に基づき、3つのテストシナリオを実行するPythonベースの負荷試験システムを設計します。

## 2. 設計方針

### 2.1 基本方針
- **モジュラー設計**: シナリオごとに独立したモジュール構成
- **設定駆動**: JSONファイルによるテスト設定管理
- **データテンプレート**: 既存のTestDataを活用したメッセージ生成
- **メトリクス収集**: リアルタイム監視とレポート出力機能
- **Docker対応**: コンテナ化による環境統一とスケールアウト

### 2.2 技術スタック
- **Python 3.11+**: メイン実装言語
- **asyncio**: 非同期処理による大量デバイスシミュレーション
- **azure-iot-device**: Azure IoT Hub SDK
- **prometheus-client**: メトリクス収集・監視
- **pydantic**: 設定管理とデータバリデーション
- **click**: CLI インターフェース

## 3. アーキテクチャ設計

### 3.1 全体構成

```
workload-test-project/
├── src/
│   ├── core/
│   │   ├── __init__.py
│   │   ├── config.py              # 設定管理
│   │   ├── device_factory.py      # デバイスファクトリー
│   │   ├── message_generator.py   # メッセージ生成
│   │   ├── metrics_collector.py   # メトリクス収集
│   │   └── test_orchestrator.py   # テスト制御
│   ├── devices/
│   │   ├── __init__.py
│   │   ├── base_device.py         # デバイスベースクラス
│   │   ├── bacnet_device.py       # BACnetデバイス
│   │   ├── hvac_device.py         # HVACデバイス
│   │   ├── environmental_device.py # 環境センサー
│   │   ├── electric_device.py     # 電気デバイス
│   │   └── behavior_device.py     # 行動センサー
│   ├── scenarios/
│   │   ├── __init__.py
│   │   ├── scenario_base.py       # シナリオベースクラス
│   │   ├── device_scaling.py      # シナリオ1：デバイス数スケーリング
│   │   ├── message_frequency.py   # シナリオ2：メッセージ頻度ストレス
│   │   └── data_size_load.py      # シナリオ3：データサイズ負荷
│   └── utils/
│       ├── __init__.py
│       ├── logger.py              # ログ管理
│       └── azure_helpers.py       # Azure関連ユーティリティ
├── configs/
│   ├── scenarios/
│   │   ├── device_scaling.json    # シナリオ1設定
│   │   ├── message_frequency.json # シナリオ2設定
│   │   └── data_size_load.json    # シナリオ3設定
│   └── base_config.json           # 共通設定
├── data/
│   ├── templates/
│   │   ├── bacnet_message.json    # BACnetメッセージテンプレート
│   │   ├── hvac_message.json      # HVACメッセージテンプレート
│   │   ├── environmental_message.json # 環境センサーテンプレート
│   │   ├── electric_message.json  # 電気デバイステンプレート
│   │   └── behavior_message.json  # 行動センサーテンプレート
│   └── device_registry/
│       └── devices.json           # デバイス登録情報
├── scripts/
│   ├── setup.py                   # 初期セットアップ
│   ├── run_scenario.py            # シナリオ実行スクリプト
│   └── generate_report.py         # レポート生成
├── docker/
│   ├── Dockerfile
│   ├── docker-compose.yaml        # スケールアウト用
│   └── prometheus.yaml            # メトリクス監視設定
├── tests/
│   ├── unit/
│   └── integration/
├── docs/
│   ├── USAGE.md                   # 使用方法
│   └── METRICS.md                 # メトリクス仕様
├── requirements.txt
├── pyproject.toml
└── README.md
```

## 4. 主要コンポーネント設計

### 4.1 設定管理（config.py）

```python
from pydantic import BaseModel, Field
from typing import Dict, List, Optional
from enum import Enum

class DeviceType(str, Enum):
    BACNET = "bacnet"
    HVAC = "hvac"
    ENVIRONMENTAL = "environmental"
    ELECTRIC = "electric"
    BEHAVIOR = "behavior"

class TestConfig(BaseModel):
    scenario_name: str
    duration_minutes: int
    device_types: List[DeviceType]
    metrics_interval_seconds: int = 30
    azure_config: Dict[str, str]

class DeviceScalingConfig(TestConfig):
    initial_devices_per_type: int = 50
    increment_devices_per_type: int = 50
    max_devices_per_type: int = 1000
    message_interval_seconds: int = 60
    step_duration_minutes: int = 30

class MessageFrequencyConfig(TestConfig):
    devices_per_type: int = 200
    interval_steps: List[int] = [60, 30, 15, 5]
    step_duration_minutes: int = 30

class DataSizeLoadConfig(TestConfig):
    devices_per_type: int = 100
    point_counts: List[int] = [10, 25, 50, 100]
    message_interval_seconds: int = 60
    step_duration_minutes: int = 30
```

### 4.2 デバイスベースクラス（base_device.py）

```python
from abc import ABC, abstractmethod
from azure.iot.device.aio import IoTHubDeviceClient
import asyncio
import json
import logging
from typing import Dict, Any, Optional
from datetime import datetime

class BaseDevice(ABC):
    def __init__(self, device_id: str, connection_string: str):
        self.device_id = device_id
        self.connection_string = connection_string
        self.client: Optional[IoTHubDeviceClient] = None
        self.is_connected = False
        self.message_count = 0
        self.error_count = 0
        self.logger = logging.getLogger(f"{self.__class__.__name__}_{device_id}")

    async def connect(self) -> bool:
        try:
            self.client = IoTHubDeviceClient.create_from_connection_string(
                self.connection_string
            )
            await self.client.connect()
            self.is_connected = True
            self.logger.info(f"Device {self.device_id} connected to IoT Hub")
            return True
        except Exception as e:
            self.logger.error(f"Connection failed for {self.device_id}: {e}")
            self.error_count += 1
            return False

    async def disconnect(self):
        if self.client and self.is_connected:
            await self.client.disconnect()
            self.is_connected = False
            self.logger.info(f"Device {self.device_id} disconnected")

    @abstractmethod
    def generate_message(self, point_count: int = 10) -> Dict[str, Any]:
        """デバイスタイプ別のメッセージ生成"""
        pass

    async def send_message(self, point_count: int = 10) -> bool:
        if not self.is_connected or not self.client:
            return False
        
        try:
            message = self.generate_message(point_count)
            await self.client.send_message(json.dumps(message))
            self.message_count += 1
            self.logger.debug(f"Message sent from {self.device_id}")
            return True
        except Exception as e:
            self.logger.error(f"Message send failed for {self.device_id}: {e}")
            self.error_count += 1
            return False

    async def run_continuous(self, interval_seconds: int, point_count: int = 10, 
                           duration_minutes: Optional[int] = None):
        """連続メッセージ送信"""
        if not await self.connect():
            return

        start_time = datetime.now()
        try:
            while True:
                if duration_minutes:
                    elapsed = (datetime.now() - start_time).total_seconds() / 60
                    if elapsed >= duration_minutes:
                        break

                await self.send_message(point_count)
                await asyncio.sleep(interval_seconds)
        finally:
            await self.disconnect()
```

### 4.3 メッセージ生成器（message_generator.py）

```python
import json
import random
from datetime import datetime, timezone, timedelta
from typing import Dict, Any, List
from pathlib import Path

class MessageGenerator:
    def __init__(self, template_dir: str = "data/templates"):
        self.template_dir = Path(template_dir)
        self._templates = {}
        self._load_templates()

    def _load_templates(self):
        """テンプレートファイル読み込み"""
        template_files = {
            "bacnet": "bacnet_message.json",
            "hvac": "hvac_message.json",
            "environmental": "environmental_message.json",
            "electric": "electric_message.json",
            "behavior": "behavior_message.json"
        }
        
        for device_type, filename in template_files.items():
            template_path = self.template_dir / filename
            if template_path.exists():
                with open(template_path, 'r', encoding='utf-8') as f:
                    self._templates[device_type] = json.load(f)

    def generate_bacnet_message(self, device_id: str, point_count: int = 10) -> Dict[str, Any]:
        """BACnetデバイスメッセージ生成"""
        template = self._templates.get("bacnet", {})
        
        # ValueString配列を指定ポイント数まで拡張
        value_strings = []
        for i in range(point_count):
            value_strings.append({
                "TimeStamp": datetime.now(timezone(timedelta(hours=9))).isoformat(),
                "BACnetDevice": 3054 + (i // 20),
                "BACnetObject": {
                    "_base": "BACnetObjectIdentifier",
                    "_value": {
                        "ObjectType": random.randint(0, 5),
                        "InstanceNo": i + 1
                    }
                },
                "Properties": {
                    "PresentValue": round(random.uniform(0, 100), 1)
                }
            })
        
        return [{
            "Device_id": device_id,
            "ValueString": value_strings
        }]

    def generate_hvac_message(self, device_id: str, point_count: int = 10) -> Dict[str, Any]:
        """HVACデバイスメッセージ生成"""
        telemetry_data = []
        for i in range(point_count):
            telemetry_data.append({
                "mode": random.choice(["Heat", "Cool", "Auto", "Fan"]),
                "fan": random.choice(["Low", "Medium", "High"]),
                "setTemp": random.randint(18, 28),
                "onOff": random.choice(["ON", "OFF"]),
                "filterSign": random.randint(0, 1),
                "swing": random.choice(["30deg", "60deg", "90deg"]),
                "ambientTemp": round(random.uniform(15, 35), 1),
                "unitName": f"Unit_{i:03d}",
                "unitId": f"{i+1:03d}"
            })
        
        return {
            "telemetryData": telemetry_data,
            "acqTime": datetime.now(timezone(timedelta(hours=9))).isoformat(),
            "connTime": datetime.now(timezone(timedelta(hours=9))).isoformat(),
            "deviceId": device_id,
            "ipAddress": "192.168.10.12"
        }

    def generate_environmental_message(self, device_id: str, point_count: int = 10) -> Dict[str, Any]:
        """環境センサーメッセージ生成"""
        sensors = []
        for i in range(point_count):
            if i % 5 == 4:  # 5個に1個は照度センサー
                sensors.append({
                    "code": f"LUM{i:03d}",
                    "type": "LUM",
                    "illuminance": random.randint(100, 2000)
                })
            else:  # それ以外はCO2センサー
                sensors.append({
                    "code": f"CO2{i:03d}",
                    "type": "CO2",
                    "co2": random.randint(400, 1500),
                    "temperature": random.randint(1800, 3000),
                    "humidity": random.randint(3000, 5000)
                })
        
        return {
            "logtimestamp": datetime.now(timezone(timedelta(hours=9))).isoformat(),
            "gateway": f"gateway_{device_id}",
            "sensors": sensors
        }

    def generate_behavior_message(self, device_id: str, point_count: int = 1) -> Dict[str, Any]:
        """行動センサーメッセージ生成"""
        return {
            "point_id": device_id,
            "value": random.randint(0, 10),
            "data": {
                "sbos_space:Name": f"Room_{random.randint(100, 999)}"
            },
            "datetime": datetime.now(timezone(timedelta(hours=9))).isoformat(),
            "building": "Engineering(Bldg.2)",
            "name": f"行動センシングカメラ{device_id[-2:]}",
            "device_id": device_id
        }

    def generate_electric_message(self, device_id: str, point_count: int = 10) -> List[Dict[str, Any]]:
        """電気デバイスメッセージ生成（想定フォーマット）"""
        telemetry_data = []
        for i in range(point_count):
            telemetry_data.append({
                "pointId": f"{device_id}_point_{i:03d}",
                "timestamp": datetime.now(timezone(timedelta(hours=9))).isoformat(),
                "voltage": round(random.uniform(95, 105), 1),
                "current": round(random.uniform(0, 50), 2),
                "power": round(random.uniform(0, 5000), 1),
                "energy": round(random.uniform(0, 1000), 2)
            })
        
        return {
            "deviceId": device_id,
            "electricTelemetry": telemetry_data,
            "timestamp": datetime.now(timezone(timedelta(hours=9))).isoformat()
        }
```

### 4.2 テストシナリオ実装

#### 4.2.1 シナリオ1：デバイス数スケーリング試験

```python
class DeviceScalingScenario:
    def __init__(self, config: DeviceScalingConfig, metrics_collector):
        self.config = config
        self.metrics = metrics_collector
        self.active_devices = {}
        
    async def execute(self):
        """段階的デバイス数増加試験"""
        current_devices = self.config.initial_devices_per_type
        
        while current_devices <= self.config.max_devices_per_type:
            # 新しいデバイスを追加
            await self._add_devices(current_devices)
            
            # 指定時間実行
            await self._run_step(self.config.step_duration_minutes)
            
            # メトリクス評価（エラー率5%超過で停止）
            if await self._evaluate_metrics():
                break
            
            current_devices += self.config.increment_devices_per_type
        
        await self._cleanup_all_devices()

    async def _add_devices(self, target_count_per_type: int):
        """指定数までデバイス追加"""
        for device_type in self.config.device_types:
            current_count = len(self.active_devices.get(device_type, []))
            for i in range(current_count, target_count_per_type):
                device_id = f"{device_type}_device_{i:04d}"
                device = await self._create_device(device_type, device_id)
                await device.connect()
                
                if device_type not in self.active_devices:
                    self.active_devices[device_type] = []
                self.active_devices[device_type].append(device)

    async def _run_step(self, duration_minutes: int):
        """テスト段階実行"""
        tasks = []
        for device_type, devices in self.active_devices.items():
            for device in devices:
                task = device.run_continuous(
                    self.config.message_interval_seconds,
                    duration_minutes=duration_minutes
                )
                tasks.append(task)
        
        await asyncio.gather(*tasks, return_exceptions=True)

    async def _evaluate_metrics(self) -> bool:
        """メトリクス評価（継続可否判定）"""
        total_messages = sum(device.message_count for devices in self.active_devices.values() for device in devices)
        total_errors = sum(device.error_count for devices in self.active_devices.values() for device in devices)
        
        error_rate = total_errors / max(total_messages, 1) if total_messages > 0 else 1.0
        self.logger.info(f"Error rate: {error_rate:.2%}")
        
        # エラー率5%超過で停止
        return error_rate > 0.05
```

#### 4.2.2 シナリオ2：メッセージ頻度ストレス試験

```python
class MessageFrequencyScenario:
    def __init__(self, config: MessageFrequencyConfig, metrics_collector):
        self.config = config
        self.metrics = metrics_collector
        self.devices = []
        
    async def execute(self):
        """メッセージ送信間隔短縮試験"""
        # 固定デバイス数で初期化
        await self._setup_devices()
        
        for interval in self.config.interval_steps:
            self.logger.info(f"Starting frequency test: {interval}s interval")
            
            # 指定間隔でテスト実行
            await self._run_frequency_test(interval)
            
            # メトリクス収集・評価
            await self._collect_step_metrics(interval)
            
            # 短時間休憩
            await asyncio.sleep(30)
        
        await self._cleanup_devices()

    async def _run_frequency_test(self, interval_seconds: int):
        """指定間隔でのメッセージ送信テスト"""
        tasks = []
        for device in self.devices:
            task = device.run_continuous(
                interval_seconds,
                duration_minutes=self.config.step_duration_minutes
            )
            tasks.append(task)
        
        await asyncio.gather(*tasks, return_exceptions=True)
```

#### 4.2.3 シナリオ3：データサイズ負荷試験

```python
class DataSizeLoadScenario:
    def __init__(self, config: DataSizeLoadConfig, metrics_collector):
        self.config = config
        self.metrics = metrics_collector
        self.devices = []
        
    async def execute(self):
        """データサイズ段階的拡大試験"""
        await self._setup_devices()
        
        for point_count in self.config.point_counts:
            self.logger.info(f"Starting data size test: {point_count} points per message")
            
            # 指定ポイント数でメッセージ送信
            await self._run_data_size_test(point_count)
            
            # メトリクス収集
            await self._collect_step_metrics(point_count)
            
            await asyncio.sleep(30)
        
        await self._cleanup_devices()

    async def _run_data_size_test(self, point_count: int):
        """指定ポイント数でのメッセージ送信テスト"""
        tasks = []
        for device in self.devices:
            task = device.run_continuous(
                self.config.message_interval_seconds,
                point_count=point_count,
                duration_minutes=self.config.step_duration_minutes
            )
            tasks.append(task)
        
        await asyncio.gather(*tasks, return_exceptions=True)
```

### 4.3 メトリクス収集（metrics_collector.py）

```python
from prometheus_client import Counter, Histogram, Gauge, start_http_server
import time
import json
from typing import Dict, Any
from pathlib import Path

class MetricsCollector:
    def __init__(self):
        # Prometheusメトリクス定義
        self.messages_sent_total = Counter(
            'loadtest_messages_sent_total',
            'Total messages sent',
            ['device_type', 'scenario']
        )
        
        self.messages_failed_total = Counter(
            'loadtest_messages_failed_total', 
            'Total failed messages',
            ['device_type', 'scenario', 'error_type']
        )
        
        self.message_send_duration = Histogram(
            'loadtest_message_send_duration_seconds',
            'Message send duration',
            ['device_type']
        )
        
        self.active_devices = Gauge(
            'loadtest_active_devices',
            'Number of active devices',
            ['device_type', 'scenario']
        )
        
        # 内部メトリクス保存
        self.scenario_metrics = {}

    def start_metrics_server(self, port: int = 8000):
        """Prometheusメトリクスサーバー起動"""
        start_http_server(port)

    def record_message_sent(self, device_type: str, scenario: str):
        """メッセージ送信記録"""
        self.messages_sent_total.labels(
            device_type=device_type, 
            scenario=scenario
        ).inc()

    def record_message_failed(self, device_type: str, scenario: str, error_type: str):
        """メッセージ送信失敗記録"""
        self.messages_failed_total.labels(
            device_type=device_type,
            scenario=scenario, 
            error_type=error_type
        ).inc()

    def record_send_duration(self, device_type: str, duration: float):
        """メッセージ送信時間記録"""
        self.message_send_duration.labels(device_type=device_type).observe(duration)

    def set_active_devices(self, device_type: str, scenario: str, count: int):
        """アクティブデバイス数設定"""
        self.active_devices.labels(
            device_type=device_type,
            scenario=scenario
        ).set(count)

    def save_scenario_metrics(self, scenario_name: str, step_name: str, metrics: Dict[str, Any]):
        """シナリオメトリクス保存"""
        if scenario_name not in self.scenario_metrics:
            self.scenario_metrics[scenario_name] = {}
        
        self.scenario_metrics[scenario_name][step_name] = {
            "timestamp": datetime.now().isoformat(),
            **metrics
        }

    def export_metrics(self, output_path: str):
        """メトリクスをJSONファイルにエクスポート"""
        with open(output_path, 'w', encoding='utf-8') as f:
            json.dump(self.scenario_metrics, f, ensure_ascii=False, indent=2)
```

### 4.4 CLI インターフェース（run_test.py）

```python
import click
import asyncio
import json
import os
from typing import List, Optional, Dict, Any

@click.command()
@click.option('--scenario', 
              type=click.Choice(['device_scaling', 'message_frequency', 'data_size_load']),
              required=True,
              help='実行するテストシナリオ')
@click.option('--step', 
              default='all',
              help='実行するステップ (all, 1, 2,3 または 1,3,4 等)')
@click.option('--device-types',
              default=None,
              help='対象デバイスタイプ (bacnet,hvac,environmental,electric,behavior)')
@click.option('--config',
              default=None,
              help='カスタム設定ファイルパス')
@click.option('--config-override',
              default=None,
              help='設定オーバーライド (JSON文字列)')
@click.option('--duration',
              type=int,
              default=None,
              help='各ステップの実行時間（分）')
@click.option('--dry-run',
              is_flag=True,
              help='ドライランモード（実際のメッセージ送信なし）')
@click.option('--debug',
              is_flag=True,
              help='デバッグモード')
@click.option('--log-level',
              type=click.Choice(['DEBUG', 'INFO', 'WARNING', 'ERROR']),
              default='INFO',
              help='ログレベル')
@click.option('--output-dir',
              default='./results',
              help='結果出力ディレクトリ')
@click.option('--metrics-port',
              type=int,
              default=8000,
              help='Prometheusメトリクスサーバーポート')
def main(scenario: str, step: str, device_types: Optional[str], config: Optional[str],
         config_override: Optional[str], duration: Optional[int], dry_run: bool,
         debug: bool, log_level: str, output_dir: str, metrics_port: int):
    """Building OS 負荷試験実行ツール"""
    
    # ログ設定
    setup_logging(log_level, debug)
    
    # 設定読み込み・マージ
    test_config = load_and_merge_config(
        scenario, config, config_override, device_types, duration
    )
    
    # 実行ステップ解析
    target_steps = parse_steps(step, test_config)
    
    # テスト実行
    orchestrator = TestOrchestrator(test_config, output_dir, metrics_port, dry_run)
    asyncio.run(orchestrator.execute_steps(target_steps))

def parse_steps(step_arg: str, config: Dict[str, Any]) -> List[int]:
    """ステップ引数を解析"""
    if step_arg.lower() == 'all':
        return [s['step_id'] for s in config['steps']]
    
    try:
        return [int(s.strip()) for s in step_arg.split(',')]
    except ValueError:
        raise click.BadParameter(f"Invalid step format: {step_arg}. Use 'all' or comma-separated numbers like '1,2,3'")

def load_and_merge_config(scenario: str, custom_config: Optional[str], 
                         config_override: Optional[str], device_types: Optional[str],
                         duration: Optional[int]) -> Dict[str, Any]:
    """設定ファイル読み込み・マージ"""
    # デフォルト設定ファイル
    default_config_path = f"configs/scenarios/{scenario}.json"
    config_path = custom_config or default_config_path
    
    with open(config_path, 'r', encoding='utf-8') as f:
        config = json.load(f)
    
    # 環境変数置換
    config = substitute_environment_variables(config)
    
    # デバイスタイプフィルタ
    if device_types:
        config['device_types'] = device_types.split(',')
    
    # 実行時間オーバーライド
    if duration:
        for step in config['steps']:
            step['duration_minutes'] = duration
    
    # JSONオーバーライド適用
    if config_override:
        override_data = json.loads(config_override)
        config = merge_dicts(config, override_data)
    
    return config

def substitute_environment_variables(config: Dict[str, Any]) -> Dict[str, Any]:
    """環境変数を設定値に置換"""
    config_str = json.dumps(config)
    
    # ${VAR_NAME} 形式の環境変数を置換
    import re
    def replace_env_var(match):
        var_name = match.group(1)
        return os.getenv(var_name, match.group(0))
    
    config_str = re.sub(r'\$\{([^}]+)\}', replace_env_var, config_str)
    return json.loads(config_str)

class TestOrchestrator:
    def __init__(self, config: Dict[str, Any], output_dir: str, metrics_port: int, dry_run: bool):
        self.config = config
        self.output_dir = output_dir
        self.metrics_port = metrics_port
        self.dry_run = dry_run
        self.metrics_collector = MetricsCollector()
        self.logger = logging.getLogger(__name__)

    async def execute_steps(self, target_steps: List[int]):
        """指定されたステップを実行"""
        # メトリクスサーバー起動
        if not self.dry_run:
            self.metrics_collector.start_metrics_server(self.metrics_port)
        
        # 実行対象ステップ抽出
        steps_to_run = [step for step in self.config['steps'] if step['step_id'] in target_steps]
        
        self.logger.info(f"Executing scenario: {self.config['scenario_name']}")
        self.logger.info(f"Target steps: {[s['step_name'] for s in steps_to_run]}")
        
        # シナリオ別実行
        scenario_type = self.config['scenario_type']
        if scenario_type == 'device_scaling':
            await self._execute_device_scaling(steps_to_run)
        elif scenario_type == 'message_frequency':
            await self._execute_message_frequency(steps_to_run)
        elif scenario_type == 'data_size_load':
            await self._execute_data_size_load(steps_to_run)
        
        # 結果出力
        await self._generate_report()

    async def _execute_device_scaling(self, steps: List[Dict]):
        """デバイススケーリングシナリオ実行"""
        scenario = DeviceScalingScenario(self.config, self.metrics_collector, self.dry_run)
        await scenario.execute_steps(steps)

    async def _generate_report(self):
        """テスト結果レポート生成"""
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        os.makedirs(self.output_dir, exist_ok=True)
        output_file = f"{self.output_dir}/{self.config['scenario_type']}_{timestamp}.json"
        
        self.metrics_collector.export_metrics(output_file)
        self.logger.info(f"Test report saved: {output_file}")
```

## 5. 設定ファイル仕様

### 5.1 シナリオ1設定例（device_scaling.json）

```json
{
  "scenario_type": "device_scaling",
  "scenario_name": "デバイス数スケーリング試験",
  "device_types": ["bacnet", "hvac", "environmental", "electric", "behavior"],
  "steps": [
    {
      "step_id": 1,
      "step_name": "初期負荷（250台）",
      "devices_per_type": 50,
      "duration_minutes": 30
    },
    {
      "step_id": 2, 
      "step_name": "中負荷（500台）",
      "devices_per_type": 100,
      "duration_minutes": 30
    },
    {
      "step_id": 3,
      "step_name": "高負荷（1000台）", 
      "devices_per_type": 200,
      "duration_minutes": 30
    },
    {
      "step_id": 4,
      "step_name": "最大負荷（5000台）",
      "devices_per_type": 1000,
      "duration_minutes": 30
    }
  ],
  "message_interval_seconds": 60,
  "metrics_interval_seconds": 30,
  "azure_config": {
    "iothub_connection_string": "${IOTHUB_CONNECTION_STRING}",
    "device_connection_strings": {
      "bacnet": "${BACNET_DEVICE_CONNECTION_STRING}",
      "hvac": "${HVAC_DEVICE_CONNECTION_STRING}",
      "environmental": "${ENV_DEVICE_CONNECTION_STRING}",
      "electric": "${ELECTRIC_DEVICE_CONNECTION_STRING}",
      "behavior": "${BEHAVIOR_DEVICE_CONNECTION_STRING}"
    }
  },
  "thresholds": {
    "error_rate_percent": 5.0,
    "timeout_seconds": 30,
    "max_response_time_ms": 5000
  }
}
```

### 5.2 シナリオ2設定例（message_frequency.json）

```json
{
  "scenario_type": "message_frequency", 
  "scenario_name": "メッセージ頻度ストレス試験",
  "device_types": ["bacnet", "hvac", "environmental", "electric", "behavior"],
  "devices_per_type": 200,
  "steps": [
    {
      "step_id": 1,
      "step_name": "通常頻度（60秒間隔）",
      "message_interval_seconds": 60,
      "duration_minutes": 30
    },
    {
      "step_id": 2,
      "step_name": "中頻度（30秒間隔）",
      "message_interval_seconds": 30,
      "duration_minutes": 30
    },
    {
      "step_id": 3,
      "step_name": "高頻度（15秒間隔）",
      "message_interval_seconds": 15,
      "duration_minutes": 30
    },
    {
      "step_id": 4,
      "step_name": "最高頻度（5秒間隔）",
      "message_interval_seconds": 5,
      "duration_minutes": 30
    }
  ],
  "metrics_interval_seconds": 10,
  "azure_config": {
    "iothub_connection_string": "${IOTHUB_CONNECTION_STRING}",
    "device_connection_strings": {
      "bacnet": "${BACNET_DEVICE_CONNECTION_STRING}",
      "hvac": "${HVAC_DEVICE_CONNECTION_STRING}",
      "environmental": "${ENV_DEVICE_CONNECTION_STRING}",
      "electric": "${ELECTRIC_DEVICE_CONNECTION_STRING}",
      "behavior": "${BEHAVIOR_DEVICE_CONNECTION_STRING}"
    }
  },
  "thresholds": {
    "error_rate_percent": 10.0,
    "timeout_seconds": 15,
    "max_response_time_ms": 3000
  }
}
```

### 5.3 シナリオ3設定例（data_size_load.json）

```json
{
  "scenario_type": "data_size_load",
  "scenario_name": "データサイズ負荷試験", 
  "device_types": ["bacnet", "hvac", "environmental", "electric"],
  "devices_per_type": 100,
  "steps": [
    {
      "step_id": 1,
      "step_name": "標準サイズ（10ポイント）",
      "point_count": 10,
      "duration_minutes": 30
    },
    {
      "step_id": 2,
      "step_name": "中サイズ（25ポイント）", 
      "point_count": 25,
      "duration_minutes": 30
    },
    {
      "step_id": 3,
      "step_name": "大サイズ（50ポイント）",
      "point_count": 50,
      "duration_minutes": 30
    },
    {
      "step_id": 4,
      "step_name": "最大サイズ（100ポイント）",
      "point_count": 100,
      "duration_minutes": 30
    }
  ],
  "message_interval_seconds": 60,
  "metrics_interval_seconds": 30,
  "azure_config": {
    "iothub_connection_string": "${IOTHUB_CONNECTION_STRING}",
    "device_connection_strings": {
      "bacnet": "${BACNET_DEVICE_CONNECTION_STRING}",
      "hvac": "${HVAC_DEVICE_CONNECTION_STRING}",
      "environmental": "${ENV_DEVICE_CONNECTION_STRING}",
      "electric": "${ELECTRIC_DEVICE_CONNECTION_STRING}"
    }
  },
  "thresholds": {
    "error_rate_percent": 5.0,
    "timeout_seconds": 60,
    "max_response_time_ms": 10000
  }
}
```

### 5.4 環境変数設定例（.env）

```bash
# Azure IoT Hub接続文字列
IOTHUB_CONNECTION_STRING="HostName=your-iothub.azure-devices.net;SharedAccessKeyName=iothubowner;SharedAccessKey=..."

# デバイス別接続文字列（複数のIoT Hubを使う場合）
BACNET_DEVICE_CONNECTION_STRING="HostName=bacnet-iothub.azure-devices.net;DeviceId=loadtest-bacnet-{id};SharedAccessKey=..."
HVAC_DEVICE_CONNECTION_STRING="HostName=hvac-iothub.azure-devices.net;DeviceId=loadtest-hvac-{id};SharedAccessKey=..."
ENV_DEVICE_CONNECTION_STRING="HostName=env-iothub.azure-devices.net;DeviceId=loadtest-env-{id};SharedAccessKey=..."
ELECTRIC_DEVICE_CONNECTION_STRING="HostName=electric-iothub.azure-devices.net;DeviceId=loadtest-electric-{id};SharedAccessKey=..."
BEHAVIOR_DEVICE_CONNECTION_STRING="HostName=behavior-iothub.azure-devices.net;DeviceId=loadtest-behavior-{id};SharedAccessKey=..."

# 共通設定
LOG_LEVEL="INFO"
METRICS_PORT=8000
PROMETHEUS_ENABLED=true
OUTPUT_DIR="./results"
```

## 6. 実行方法

### 6.1 CLI インターフェース

```bash
# 基本的な実行方法
python scripts/run_test.py --scenario device_scaling --step all

# 特定ステップのみ実行
python scripts/run_test.py --scenario device_scaling --step 1  # 250台のみ
python scripts/run_test.py --scenario message_frequency --step 2  # 30秒間隔のみ
python scripts/run_test.py --scenario data_size_load --step 3  # 50ポイントのみ

# カスタム設定ファイル指定
python scripts/run_test.py --scenario device_scaling --config custom_config.json

# デバイスタイプ指定
python scripts/run_test.py --scenario device_scaling --device-types bacnet hvac

# デバッグモード
python scripts/run_test.py --scenario device_scaling --debug --log-level INFO

# ドライランモード（実際のメッセージ送信なし）
python scripts/run_test.py --scenario device_scaling --dry-run

# 完全なコマンド例
python scripts/run_test.py \
  --scenario device_scaling \
  --step 1,2,3 \
  --device-types bacnet hvac environmental \
  --duration 15 \
  --config-override '{"max_devices_per_type": 500}' \
  --output-dir ./results/custom_run
```

### 6.2 環境変数設定とDocker実行

```bash
# 環境変数ファイル設定（.env）
export IOTHUB_CONNECTION_STRING="HostName=your-iothub.azure-devices.net;SharedAccessKeyName=iothubowner;SharedAccessKey=..."
export BACNET_DEVICE_CONNECTION_STRING="HostName=bacnet-iothub.azure-devices.net;DeviceId=loadtest-bacnet-{id};SharedAccessKey=..."

# Docker コンテナでの実行
docker run --env-file .env \
  building-os-loadtest \
  --scenario device_scaling --step all

# Docker Compose でのスケールアウト実行
docker-compose up --scale loadtest=3

# 特定シナリオ・ステップのみ起動
docker-compose run loadtest --scenario message_frequency --step 2,3 --device-types bacnet,hvac

# カスタム設定での実行
docker-compose run -v $(pwd)/custom_config.json:/app/config.json loadtest \
  --scenario device_scaling --config /app/config.json --duration 15
```

### 6.3 実行例

#### 段階的負荷テスト実行例

```bash
# ステップ1のみ実行（初期負荷確認）
python scripts/run_test.py --scenario device_scaling --step 1 --debug

# ステップ2-4を順次実行（段階的負荷増加）
python scripts/run_test.py --scenario device_scaling --step 2,3,4

# 特定デバイスタイプのみでクイックテスト
python scripts/run_test.py --scenario device_scaling --step 1,2 \
  --device-types bacnet,environmental --duration 10

# 本格的な負荷試験実行
python scripts/run_test.py --scenario device_scaling --step all \
  --output-dir ./results/full_test_$(date +%Y%m%d)
```

#### Docker Compose 設定

```yaml
# docker-compose.yaml
version: '3.8'
services:
  loadtest:
    build: ./docker
    env_file: .env
    environment:
      - LOG_LEVEL=${LOG_LEVEL:-INFO}
      - METRICS_PORT=${METRICS_PORT:-8000}
      - OUTPUT_DIR=/app/results
    volumes:
      - ./results:/app/results
      - ./configs:/app/configs
    ports:
      - "${METRICS_PORT:-8000}:8000"
    command: ["python", "scripts/run_test.py", "--scenario", "device_scaling", "--step", "all"]

  # 複数コンテナでの並列実行例
  loadtest-bacnet:
    extends: loadtest
    command: ["python", "scripts/run_test.py", "--scenario", "device_scaling", "--device-types", "bacnet", "--step", "all"]
    
  loadtest-hvac:
    extends: loadtest  
    command: ["python", "scripts/run_test.py", "--scenario", "device_scaling", "--device-types", "hvac", "--step", "all"]

  prometheus:
    image: prom/prometheus:latest
    ports:
      - "9090:9090"
    volumes:
      - ./docker/prometheus.yaml:/etc/prometheus/prometheus.yml

  grafana:
    image: grafana/grafana:latest
    ports:
      - "3000:3000"
    environment:
      - GF_SECURITY_ADMIN_PASSWORD=admin
```

## 7. メトリクス・監視

### 7.1 収集メトリクス

**パフォーマンスメトリクス**:
- `loadtest_messages_sent_total`: 送信メッセージ総数
- `loadtest_messages_failed_total`: 送信失敗メッセージ数
- `loadtest_message_send_duration_seconds`: メッセージ送信時間
- `loadtest_active_devices`: アクティブデバイス数
- `loadtest_error_rate`: エラー率
- `loadtest_throughput_messages_per_second`: スループット

**Azure固有メトリクス** (Azure Monitor API経由):
- Azure Functions実行時間・同時実行数
- CosmosDB RU消費量・スロットリング率
- IoT Hub受信スループット・メッセージ遅延

### 7.2 レポート出力

```python
class ReportGenerator:
    def generate_excel_report(self, scenario_metrics: Dict, output_path: str):
        """Excel形式のレポート生成"""
        # パフォーマンス推移グラフ
        # エラー率分析
        # ボトルネック分析
        # 推奨運用パラメータ
        pass

    def generate_summary_report(self, all_scenarios: List[Dict]) -> Dict:
        """全シナリオ統合サマリーレポート"""
        return {
            "max_concurrent_devices": self._calculate_max_devices(all_scenarios),
            "optimal_message_interval": self._calculate_optimal_interval(all_scenarios),
            "max_message_size": self._calculate_max_message_size(all_scenarios),
            "bottleneck_components": self._identify_bottlenecks(all_scenarios),
            "recommended_scaling_parameters": self._recommend_scaling(all_scenarios)
        }
```

## 8. 拡張性・運用考慮事項

### 8.1 スケーラビリティ
- **水平スケーリング**: Docker Composeでの複数コンテナ起動
- **負荷分散**: デバイス数をコンテナ間で分散
- **リソース制御**: CPU・メモリ制限設定

### 8.2 監視・観測性
- **Prometheus**: メトリクス収集・可視化
- **Grafana**: リアルタイムダッシュボード
- **構造化ログ**: JSON形式でのログ出力
- **アラート**: 異常検知時の通知機能

### 8.3 エラー処理・回復性
- **指数バックオフ**: 再試行戦略
- **サーキットブレーカー**: 障害時の自動停止
- **グレースフルシャットダウン**: 安全な終了処理
- **部分失敗許容**: 一部デバイス障害でもテスト継続

## 9. セキュリティ考慮事項

### 9.1 認証・認可
- **Azure IoT Hub**: SAS トークンベース認証
- **接続文字列**: 環境変数での管理
- **デバイス証明書**: X.509証明書サポート（オプション）

### 9.2 データ保護
- **機密情報**: 接続文字列の暗号化
- **ログ**: 機密データのマスキング
- **ネットワーク**: TLS通信の強制

## 10. CLI コマンドリファレンス

### 10.1 主要オプション

| オプション | 説明 | 例 |
|-----------|------|-----|
| `--scenario` | テストシナリオ指定（必須） | `device_scaling`, `message_frequency`, `data_size_load` |
| `--step` | 実行ステップ指定 | `all`, `1`, `1,3`, `2,3,4` |
| `--device-types` | 対象デバイスタイプ | `bacnet,hvac`, `environmental` |
| `--config` | カスタム設定ファイル | `./custom/my_config.json` |
| `--duration` | ステップ実行時間（分） | `15`, `30` |
| `--dry-run` | ドライランモード | フラグオプション |
| `--debug` | デバッグモード | フラグオプション |
| `--output-dir` | 結果出力ディレクトリ | `./results/test_20250903` |

### 10.2 設定オーバーライド例

```bash
# デバイス数を一時的に変更
python scripts/run_test.py --scenario device_scaling --step 1 \
  --config-override '{"steps":[{"step_id":1,"devices_per_type":25,"duration_minutes":10}]}'

# しきい値を一時的に変更
python scripts/run_test.py --scenario message_frequency --step all \
  --config-override '{"thresholds":{"error_rate_percent":15.0}}'
```

### 10.3 実行結果例

```bash
$ python scripts/run_test.py --scenario device_scaling --step 1,2 --debug

2025-09-03 14:30:00 [INFO] Loading scenario config: device_scaling
2025-09-03 14:30:00 [INFO] Target steps: ['初期負荷（250台）', '中負荷（500台）']
2025-09-03 14:30:00 [INFO] Device types: ['bacnet', 'hvac', 'environmental', 'electric', 'behavior']
2025-09-03 14:30:00 [INFO] Starting Prometheus metrics server on port 8000
2025-09-03 14:30:01 [INFO] Step 1: 初期負荷（250台）starting...
2025-09-03 14:30:01 [INFO] Creating 50 devices per type...
2025-09-03 14:30:05 [INFO] All devices connected. Starting message transmission...
2025-09-03 15:00:01 [INFO] Step 1 completed. Messages: 15000, Errors: 12 (0.08%)
2025-09-03 15:00:01 [INFO] Step 2: 中負荷（500台）starting...
...
2025-09-03 15:30:01 [INFO] Test completed. Report saved: ./results/device_scaling_20250903_143000.json
```

## 11. 実装優先度

### Phase 1（最小実装）
1. CLI インターフェース（run_test.py）とコマンド解析
2. 設定管理システム（環境変数置換・ステップ制御）
3. ベースデバイスクラスと基本的なメッセージ生成
4. シナリオ1（デバイス数スケーリング）の実装
5. 基本的なメトリクス収集とJSONレポート出力

### Phase 2（機能拡張）
1. シナリオ2・3の実装
2. Prometheus/Grafana統合
3. Docker化とマルチコンテナ対応
4. Azure Monitor API統合（Functions, CosmosDB メトリクス）

### Phase 3（運用強化）
1. 自動レポート生成（Excel）とグラフ作成
2. CI/CD統合と自動実行
3. アラート機能とSlack通知
4. リアルタイムダッシュボード

この設計により、柔軟なコマンドライン制御と環境変数による設定管理を備えた負荷試験システムで、Building OS の性能限界を体系的に特定できます。