import asyncio
from datetime import datetime
from typing import List, Dict, Any, Optional

from src.core.config import DeviceScalingConfig, DeviceScalingStep
from src.core.device_factory import DeviceFactory
from src.core.metrics_collector import MetricsCollector
from src.core.shared_connection_device import SharedConnectionDevice, VirtualDevice
from src.utils.logger import get_logger


class DeviceScalingScenario:
    """デバイススケーリングシナリオ（共有接続版）"""
    
    def __init__(self, config: DeviceScalingConfig, device_factory: DeviceFactory,
                 metrics_collector: MetricsCollector, dry_run: bool = False):
        self.config = config
        self.device_factory = device_factory
        self.metrics_collector = metrics_collector
        self.dry_run = dry_run
        self.logger = get_logger(__name__)
        
        # 共有接続管理（デバイスタイプごと）
        self.shared_connections: Dict[str, SharedConnectionDevice] = {}
        # 実行中のデバイス管理（ステップIDごと）
        self.active_devices: Dict[int, List[VirtualDevice]] = {}
        self.device_tasks: Dict[int, List[asyncio.Task]] = {}

    async def execute_step(self, step_id: int) -> Dict[str, Any]:
        """指定されたステップを実行"""
        step_config = self._get_step_config(step_id)
        if not step_config:
            raise ValueError(f"Step {step_id} not found")
        
        self.logger.info(f"Executing device scaling step {step_id}")
        
        start_time = datetime.now()
        device_types = self.config.device_types
        total_devices = step_config.devices_per_type * len(device_types)
        
        try:
            # 新規デバイス作成・起動
            await self._create_and_start_devices(step_id, step_config)
            
            # ステップ実行時間待機
            duration_minutes = step_config.duration_minutes
            self.logger.info(f"Running step {step_id} for {duration_minutes} minutes")
            
            # 定期的な状態確認
            await self._monitor_step_execution(step_id, duration_minutes)
            
            # メトリクス記録
            await self._record_step_metrics(step_id, step_config)
            
        except Exception as e:
            self.logger.error(f"Step {step_id} execution failed: {e}")
            raise
        
        finally:
            # このステップで追加されたデバイスを停止
            await self._stop_step_devices(step_id)
        
        end_time = datetime.now()
        
        result = {
            'step_id': step_id,
            'device_count': total_devices,
            'device_types': device_types,
            'duration_minutes': step_config.duration_minutes,
            'start_time': start_time.isoformat(),
            'end_time': end_time.isoformat(),
            'success': True
        }
        
        self.logger.info(f"Step {step_id} completed successfully")
        return result

    async def _create_and_start_devices(self, step_id: int, step_config: DeviceScalingStep):
        """デバイス作成・起動（共有接続使用）"""
        device_types = self.config.device_types

        step_devices = []
        step_tasks = []
        
        for i, device_type in enumerate(device_types):
            device_count = step_config.devices_per_type
            
            if device_count == 0:
                continue
            
            self.logger.info(f"Creating {device_count} {device_type} virtual devices for step {step_id}")
            
            # 共有接続がまだない場合は作成
            if device_type not in self.shared_connections:
                primary_device_id = f"scaling-{device_type}-primary"
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
            
            shared_connection = self.shared_connections[device_type]
            
            # 仮想デバイス作成
            id_prefix = f"step{step_id}-{device_type}"
            devices = self.device_factory.create_virtual_devices_batch(
                device_type=device_type,
                count=device_count,
                shared_connection=shared_connection,
                id_prefix=id_prefix
            )
            
            step_devices.extend(devices)
            
            # デバイス起動（ドライランでない場合）
            if not self.dry_run:
                # メッセージ送信タイミングを分散させるため、初期遅延を設定
                # interval_secondsの範囲内でランダムに遅延
                initial_delay_max = self.config.message_interval_seconds
                
                for device in devices:
                    task = asyncio.create_task(
                        device.run_continuous(
                            interval_seconds=self.config.message_interval_seconds,
                            duration_minutes=step_config.duration_minutes,
                            dry_run=self.dry_run,
                            initial_delay_max=initial_delay_max
                        )
                    )
                    step_tasks.append(task)
        
        # 管理用辞書に追加
        self.active_devices[step_id] = step_devices
        self.device_tasks[step_id] = step_tasks
        
        self.logger.info(
            f"Step {step_id}: Created and started {len(step_devices)} virtual devices "
            f"using {len(self.shared_connections)} shared connections"
        )

    async def _monitor_step_execution(self, step_id: int, duration_minutes: int):
        """ステップ実行モニタリング"""
        if self.dry_run:
            self.logger.info(f"[DRY RUN] Would monitor step {step_id} for {duration_minutes} minutes")
            return
        
        check_interval = 30  # 30秒間隔でチェック
        total_checks = (duration_minutes * 60) // check_interval
        
        for i in range(total_checks):
            await asyncio.sleep(check_interval)
            
            # アクティブなタスク数確認
            active_tasks = [t for t in self.device_tasks.get(step_id, []) if not t.done()]
            failed_tasks = [t for t in self.device_tasks.get(step_id, []) if t.done() and t.exception()]
            
            self.logger.info(f"Step {step_id} progress: {len(active_tasks)} active, {len(failed_tasks)} failed")
            
            # メトリクス更新
            await self.metrics_collector.record_active_devices(step_id, len(active_tasks))

    async def _record_step_metrics(self, step_id: int, step_config: DeviceScalingStep):
        """ステップメトリクス記録"""
        device_types = self.config.device_types
        total_devices = step_config.devices_per_type * len(device_types)
        
        metrics = {
            'step_id': step_id,
            'device_count': total_devices,
            'device_types': device_types,
            'duration_minutes': step_config.duration_minutes,
            'timestamp': datetime.now().isoformat()
        }
        
        await self.metrics_collector.record_step_completion(step_id, metrics)

    async def _stop_step_devices(self, step_id: int):
        """ステップのデバイス停止（仮想デバイスは切断不要）"""
        devices = self.active_devices.get(step_id, [])
        tasks = self.device_tasks.get(step_id, [])
        
        if not self.dry_run and tasks:
            self.logger.info(f"Stopping {len(tasks)} device tasks for step {step_id}")
            
            # タスクキャンセル
            for task in tasks:
                if not task.done():
                    task.cancel()
            
            # タスク完了待機
            await asyncio.gather(*tasks, return_exceptions=True)
        
        # 仮想デバイスは個別の切断不要（共有接続はcleanupで切断）
        
        # 管理辞書からクリア
        if step_id in self.active_devices:
            del self.active_devices[step_id]
        if step_id in self.device_tasks:
            del self.device_tasks[step_id]
        
        self.logger.info(f"Step {step_id} devices stopped and cleaned up")

    def _get_step_config(self, step_id: int) -> Optional[DeviceScalingStep]:
        """ステップ設定取得"""
        for step in self.config.steps:
            if step.step_id == step_id:
                return step
        return None

    async def cleanup(self):
        """全リソースクリーンアップ"""
        self.logger.info("Starting orchestrator cleanup")
        
        # 全ステップのデバイス停止
        for step_id in list(self.active_devices.keys()):
            await self._stop_step_devices(step_id)
        
        # 共有接続切断
        for device_type, shared_connection in self.shared_connections.items():
            try:
                await shared_connection.disconnect()
                self.logger.info(f"Disconnected shared connection for {device_type}")
            except Exception as e:
                self.logger.error(f"Failed to disconnect shared connection for {device_type}: {e}")
        
        self.shared_connections.clear()
        
        # メトリクス収集器停止
        await self.metrics_collector.stop()
        
        self.logger.info("Orchestrator cleanup completed")