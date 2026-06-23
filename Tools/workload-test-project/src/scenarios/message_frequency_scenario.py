import asyncio
from datetime import datetime
from typing import List, Dict, Any, Optional

from src.core.config import MessageFrequencyConfig, MessageFrequencyStep
from src.core.device_factory import DeviceFactory
from src.core.metrics_collector import MetricsCollector
from src.core.shared_connection_device import SharedConnectionDevice, VirtualDevice
from src.utils.logger import get_logger


class MessageFrequencyScenario:
    """メッセージ頻度負荷シナリオ（共有接続版）"""
    
    def __init__(self, config: MessageFrequencyConfig, device_factory: DeviceFactory,
                 metrics_collector: MetricsCollector, dry_run: bool = False):
        self.config = config
        self.device_factory = device_factory
        self.metrics_collector = metrics_collector
        self.dry_run = dry_run
        self.logger = get_logger(__name__)
        
        # 共有接続管理
        self.shared_connections: Dict[str, SharedConnectionDevice] = {}
        # 仮想デバイス管理
        self.virtual_devices: List[VirtualDevice] = []
        self.device_tasks: List[asyncio.Task] = []

    async def execute_step(self, step_id: int) -> Dict[str, Any]:
        """指定されたステップを実行"""
        step_config = self._get_step_config(step_id)
        if not step_config:
            raise ValueError(f"Step {step_id} not found")
        
        self.logger.info(f"Executing message frequency step {step_id}")
        
        start_time = datetime.now()
        
        try:
            # 初回実行の場合はデバイス作成
            if step_id == 1:
                await self._create_devices()
            
            # メッセージ頻度変更・実行
            await self._execute_frequency_test(step_id, step_config)
            
            # メトリクス記録
            await self._record_step_metrics(step_id, step_config)
            
        except Exception as e:
            self.logger.error(f"Step {step_id} execution failed: {e}")
            raise
        
        end_time = datetime.now()
        
        total_devices = self.config.devices_per_type * len(self.config.device_types)
        result = {
            'step_id': step_id,
            'device_count': total_devices,
            'message_interval_seconds': step_config.message_interval_seconds,
            'duration_minutes': step_config.duration_minutes,
            'start_time': start_time.isoformat(),
            'end_time': end_time.isoformat(),
            'success': True
        }
        
        self.logger.info(f"Step {step_id} completed successfully")
        return result

    async def _create_devices(self):
        """テスト用デバイス作成（共有接続使用）"""
        total_devices = self.config.devices_per_type * len(self.config.device_types)
        self.logger.info(f"Creating {total_devices} virtual devices for message frequency test (using shared connections)")
        
        devices_per_type = self.config.devices_per_type
        
        for i, device_type in enumerate(self.config.device_types):
            device_count = devices_per_type
            
            if device_count == 0:
                continue
            
            # 各デバイスタイプに1つの共有接続を作成
            primary_device_id = f"msgfreq-{device_type}-primary"
            shared_connection = self.device_factory.create_shared_connection(
                device_type=device_type,
                primary_device_id=primary_device_id
            )
            
            # 共有接続を確立（ドライランでない場合）
            if not self.dry_run:
                if not await shared_connection.connect():
                    self.logger.error(f"Failed to establish shared connection for {device_type}")
                    continue
            
            self.shared_connections[device_type] = shared_connection
            
            # 共有接続を使用する仮想デバイスを作成
            id_prefix = f"msgfreq-{device_type}"
            virtual_devices = self.device_factory.create_virtual_devices_batch(
                device_type=device_type,
                count=device_count,
                shared_connection=shared_connection,
                id_prefix=id_prefix
            )
            
            self.virtual_devices.extend(virtual_devices)
        
        self.logger.info(
            f"Created {len(self.virtual_devices)} virtual devices using "
            f"{len(self.shared_connections)} shared connections"
        )

    async def _execute_frequency_test(self, step_id: int, step_config: MessageFrequencyStep):
        """メッセージ頻度テスト実行"""
        if self.dry_run:
            self.logger.info(f"[DRY RUN] Would execute frequency test with "
                           f"{step_config.message_interval_seconds}s interval for "
                           f"{step_config.duration_minutes} minutes")
            return
        
        # 既存タスクを停止
        await self._stop_current_tasks()
        
        # 新しい頻度でデバイスタスク開始
        self.logger.info(f"Starting virtual devices with {step_config.message_interval_seconds}s interval")
        
        for device in self.virtual_devices:
            task = asyncio.create_task(
                device.run_continuous(
                    interval_seconds=step_config.message_interval_seconds,
                    duration_minutes=step_config.duration_minutes,
                    dry_run=self.dry_run,
                    initial_delay_max=step_config.message_interval_seconds
                )
            )
            self.device_tasks.append(task)
        
        # ステップ実行時間待機
        await asyncio.sleep(step_config.duration_minutes * 60)

    async def _stop_current_tasks(self):
        """現在実行中のデバイスタスクを停止"""
        if not self.device_tasks:
            return
        
        self.logger.info(f"Stopping {len(self.device_tasks)} current device tasks")
        
        for task in self.device_tasks:
            if not task.done():
                task.cancel()
        
        await asyncio.gather(*self.device_tasks, return_exceptions=True)
        self.device_tasks.clear()

    async def _record_step_metrics(self, step_id: int, step_config: MessageFrequencyStep):
        """ステップメトリクス記録"""
        metrics = {
            'step_id': step_id,
            'device_count': len(self.virtual_devices),
            'shared_connection_count': len(self.shared_connections),
            'message_interval_seconds': step_config.message_interval_seconds,
            'duration_minutes': step_config.duration_minutes,
            'timestamp': datetime.now().isoformat()
        }
        
        await self.metrics_collector.record_step_completion(step_id, metrics)

    def _get_step_config(self, step_id: int) -> Optional[MessageFrequencyStep]:
        """ステップ設定取得"""
        for step in self.config.steps:
            if step.step_id == step_id:
                return step
        return None

    async def cleanup(self):
        """リソースクリーンアップ"""
        self.logger.info("Starting message frequency scenario cleanup")
        
        # 全タスク停止
        await self._stop_current_tasks()
        
        # 共有接続切断
        for device_type, shared_connection in self.shared_connections.items():
            try:
                await shared_connection.disconnect()
                self.logger.info(f"Disconnected shared connection for {device_type}")
            except Exception as e:
                self.logger.error(f"Failed to disconnect shared connection for {device_type}: {e}")
        
        self.virtual_devices.clear()
        self.shared_connections.clear()
        self.logger.info("Message frequency scenario cleanup completed")