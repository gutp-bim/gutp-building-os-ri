import asyncio
import os
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import List, Dict, Any, Optional

from src.core.config import BaseTestConfig, DeviceType
from src.core.device_factory import DeviceFactory
from src.core.metrics_collector import MetricsCollector
from src.core.azure_metrics_collector import AzureMetricsCollector
from src.scenarios.device_scaling_scenario import DeviceScalingScenario
from src.scenarios.message_frequency_scenario import MessageFrequencyScenario
from src.scenarios.data_size_load_scenario import DataSizeLoadScenario
from src.utils.logger import get_logger


class TestOrchestrator:
    """テスト実行統括クラス"""
    
    def __init__(self, config: BaseTestConfig, output_dir: str, 
                 metrics_port: int = 8000, dry_run: bool = False,
                 enable_azure_metrics: bool = True):
        self.config = config
        
        # 出力ディレクトリパス解決
        output_path = Path(output_dir)
        if not output_path.is_absolute():
            # 相対パスの場合、プロジェクトルートを基準に解決
            project_root = Path(__file__).parent.parent.parent
            self.output_dir = project_root / output_dir
        else:
            self.output_dir = output_path
            
        self.metrics_port = metrics_port
        self.dry_run = dry_run
        self.enable_azure_metrics = enable_azure_metrics
        self.logger = get_logger(__name__)
        
        # 出力ディレクトリ作成
        self.output_dir.mkdir(parents=True, exist_ok=True)
        
        # メトリクス収集器初期化
        self.metrics_collector = MetricsCollector(
            port=metrics_port,
            output_dir=str(self.output_dir)
        )
        
        # Azure Monitor メトリクス収集器初期化
        self.azure_metrics_collector = None
        if self.enable_azure_metrics and not dry_run:
            self.azure_metrics_collector = self._init_azure_metrics_collector()
        
        # デバイスファクトリー初期化
        device_connections = self._get_device_connections()
        max_concurrent_sends = getattr(config, 'max_concurrent_sends', 50)
        self.logger.info(f"Initializing DeviceFactory with max_concurrent_sends={max_concurrent_sends}")
        self.device_factory = DeviceFactory(
            device_connections,
            max_concurrent_sends=max_concurrent_sends
        )
        
        # シナリオマップ
        self.scenario_map = {
            "device_scaling": DeviceScalingScenario,
            "message_frequency": MessageFrequencyScenario,
            "data_size_load": DataSizeLoadScenario
        }

    def _get_device_connections(self) -> Dict[str, str]:
        """環境変数からデバイス接続文字列を取得"""
        connections = {}
        
        for device_type in DeviceType:
            env_var = f"IOTHUB_CONNECTION_STRING_{device_type.upper()}"
            connection_string = os.getenv(env_var)
            
            if not connection_string:
                # デフォルト環境変数も試行
                default_env = "IOTHUB_CONNECTION_STRING_DEFAULT"
                connection_string = os.getenv(default_env)
                
            if not connection_string:
                raise ValueError(f"Connection string not found for {device_type}. "
                               f"Set {env_var} or IOTHUB_CONNECTION_STRING_DEFAULT")
            
            connections[device_type] = connection_string
            
        return connections

    def _init_azure_metrics_collector(self) -> Optional[AzureMetricsCollector]:
        """Azure Monitor メトリクス収集器を初期化"""
        try:
            # 環境変数から設定取得
            subscription_id = os.getenv("AZURE_SUBSCRIPTION_ID")
            if not subscription_id:
                self.logger.warning("AZURE_SUBSCRIPTION_ID not set. Azure metrics collection disabled.")
                return None

            # リソースIDを環境変数から取得
            function_resource_ids = []
            cosmosdb_resource_ids = []
            iothub_resource_ids = []

            # Azure Functions
            function_rg = os.getenv("FUNCTION_RESOURCE_GROUP")
            function_name = os.getenv("FUNCTION_APP_NAME")
            if function_rg and function_name:
                function_rid = (
                    f"/subscriptions/{subscription_id}/"
                    f"resourceGroups/{function_rg}/"
                    f"providers/Microsoft.Web/sites/{function_name}"
                )
                function_resource_ids.append(function_rid)

            # CosmosDB
            cosmosdb_rg = os.getenv("COSMOSDB_RESOURCE_GROUP")
            cosmosdb_account = os.getenv("COSMOSDB_ACCOUNT_NAME")
            if cosmosdb_rg and cosmosdb_account:
                cosmosdb_rid = (
                    f"/subscriptions/{subscription_id}/"
                    f"resourceGroups/{cosmosdb_rg}/"
                    f"providers/Microsoft.DocumentDB/databaseAccounts/{cosmosdb_account}"
                )
                cosmosdb_resource_ids.append(cosmosdb_rid)

            # IoT Hub
            iothub_rg = os.getenv("IOTHUB_RESOURCE_GROUP")
            iothub_name = os.getenv("IOTHUB_NAME")
            if iothub_rg and iothub_name:
                iothub_rid = (
                    f"/subscriptions/{subscription_id}/"
                    f"resourceGroups/{iothub_rg}/"
                    f"providers/Microsoft.Devices/IotHubs/{iothub_name}"
                )
                iothub_resource_ids.append(iothub_rid)

            if not (function_resource_ids or cosmosdb_resource_ids or iothub_resource_ids):
                self.logger.warning("No Azure resources configured. Azure metrics collection disabled.")
                return None

            # Azure メトリクス収集器初期化
            collector = AzureMetricsCollector(
                subscription_id=subscription_id,
                function_resource_ids=function_resource_ids if function_resource_ids else None,
                cosmosdb_resource_ids=cosmosdb_resource_ids if cosmosdb_resource_ids else None,
                iothub_resource_ids=iothub_resource_ids if iothub_resource_ids else None,
                use_environment_credential=True
            )

            self.logger.info("Azure metrics collector initialized successfully")
            return collector

        except Exception as e:
            self.logger.warning(f"Failed to initialize Azure metrics collector: {e}")
            return None

    async def execute_steps(self, target_steps: List[int]) -> Dict[str, Any]:
        """指定されたステップを実行"""
        self.logger.info(f"Starting test execution: {self.config.scenario_name}")
        
        # メトリクス収集開始
        await self.metrics_collector.start()
        
        # Azure メトリクス収集タスク
        azure_metrics_task = None
        if self.azure_metrics_collector:
            azure_metrics_task = asyncio.create_task(
                self.azure_metrics_collector.start_continuous_collection(interval_seconds=60)
            )
            self.logger.info("Azure metrics collection started")
        
        start_time = datetime.now()
        results = []
        
        try:
            # シナリオクラス取得
            scenario_class = self.scenario_map.get(self.config.scenario_type)
            if not scenario_class:
                raise ValueError(f"Unknown scenario type: {self.config.scenario_type}")
            
            # シナリオインスタンス作成
            scenario = scenario_class(
                config=self.config,
                device_factory=self.device_factory,
                metrics_collector=self.metrics_collector,
                dry_run=self.dry_run
            )
            
            # ステップ実行
            for step_id in target_steps:
                step_config = self._get_step_config(step_id)
                if not step_config:
                    self.logger.error(f"Step {step_id} not found in configuration")
                    continue
                    
                self.logger.info(f"Executing step {step_id}: {step_config.get('description', 'No description')}")
                
                step_start = datetime.now()
                
                # Azure メトリクスにステップ情報を設定
                if self.azure_metrics_collector:
                    # 現在のタスクを停止
                    if azure_metrics_task:
                        self.azure_metrics_collector.stop_continuous_collection()
                        try:
                            await asyncio.wait_for(azure_metrics_task, timeout=5.0)
                        except asyncio.TimeoutError:
                            self.logger.warning("Azure metrics task did not stop in time")
                    
                    # 新しいステップで再開
                    step_name = step_config.get('description', f'Step {step_id}')
                    azure_metrics_task = asyncio.create_task(
                        self.azure_metrics_collector.start_continuous_collection(
                            interval_seconds=60,
                            step_id=step_id,
                            step_name=step_name
                        )
                    )
                
                step_result = await scenario.execute_step(step_id)
                step_end = datetime.now()
                
                step_result.update({
                    'step_id': step_id,
                    'start_time': step_start.isoformat(),
                    'end_time': step_end.isoformat(),
                    'duration_seconds': (step_end - step_start).total_seconds()
                })
                
                results.append(step_result)
                self.logger.info(f"Step {step_id} completed in {step_result['duration_seconds']:.2f} seconds")
        
        except Exception as e:
            self.logger.error(f"Test execution failed: {e}")
            raise
        
        finally:
            # メトリクス収集停止
            await self.metrics_collector.stop()
            
            # Azure メトリクス収集停止
            if self.azure_metrics_collector:
                self.azure_metrics_collector.stop_continuous_collection()
                if azure_metrics_task:
                    try:
                        await asyncio.wait_for(azure_metrics_task, timeout=10.0)
                    except asyncio.TimeoutError:
                        self.logger.warning("Azure metrics task did not stop in time")
                
                # Azure メトリクスをエクスポート
                await self._export_azure_metrics()
            
        end_time = datetime.now()
        
        # 結果サマリー作成
        summary = {
            'scenario': self.config.scenario_name,
            'scenario_type': self.config.scenario_type,
            'start_time': start_time.isoformat(),
            'end_time': end_time.isoformat(),
            'total_duration_seconds': (end_time - start_time).total_seconds(),
            'executed_steps': target_steps,
            'step_results': results,
            'metrics_file': self.metrics_collector.get_output_file(),
            'dry_run': self.dry_run
        }
        
        # Azure メトリクスファイル情報を追加
        if self.azure_metrics_collector:
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            summary['azure_metrics'] = {
                'raw_data': f"{self.config.scenario_type}_azure_metrics_raw_{timestamp}.csv",
                'summary': f"{self.config.scenario_type}_azure_metrics_summary_{timestamp}.csv",
                'graphs': f"{self.config.scenario_type}_graphs_{timestamp}"
            }
        
        # 結果ファイル保存
        await self._save_results(summary)
        
        self.logger.info(f"Test completed successfully. Results saved to {self.output_dir}")
        return summary

    def _get_step_config(self, step_id: int) -> Optional[Dict[str, Any]]:
        """ステップ設定を取得"""
        for step in self.config.steps:
            if step.step_id == step_id:
                return step.dict()
        return None

    async def _export_azure_metrics(self):
        """Azure メトリクスをエクスポート"""
        if not self.azure_metrics_collector:
            return
        
        try:
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            metrics_dir = self.output_dir / "metrics"
            metrics_dir.mkdir(parents=True, exist_ok=True)
            
            # Raw データをCSVにエクスポート
            raw_csv_path = metrics_dir / f"{self.config.scenario_type}_azure_metrics_raw_{timestamp}.csv"
            self.logger.info(f"Exporting Azure metrics raw data to {raw_csv_path}")
            self.azure_metrics_collector.export_to_csv(str(raw_csv_path))
            
            # サマリーをCSVにエクスポート
            summary_csv_path = metrics_dir / f"{self.config.scenario_type}_azure_metrics_summary_{timestamp}.csv"
            self.logger.info(f"Exporting Azure metrics summary to {summary_csv_path}")
            self.azure_metrics_collector.export_summary_to_csv(str(summary_csv_path))
            
            # グラフを生成
            graphs_dir = metrics_dir / f"{self.config.scenario_type}_graphs_{timestamp}"
            self.logger.info(f"Generating Azure metrics graphs in {graphs_dir}")
            self.azure_metrics_collector.generate_graphs(str(graphs_dir))
            
            self.logger.info("Azure metrics export completed successfully")
            
        except Exception as e:
            self.logger.error(f"Failed to export Azure metrics: {e}", exc_info=True)

    async def _save_results(self, summary: Dict[str, Any]):
        """結果をJSONファイルに保存"""
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        result_file = self.output_dir / f"{self.config.scenario_type}_test_result_{self.config.scenario_type}_{timestamp}.json"
        
        with open(result_file, 'w', encoding='utf-8') as f:
            json.dump(summary, f, ensure_ascii=False, indent=2)
        
        self.logger.info(f"Test results saved to {result_file}")

    async def cleanup(self):
        """リソースクリーンアップ"""
        if hasattr(self, 'metrics_collector'):
            await self.metrics_collector.stop()
        
        if hasattr(self, 'azure_metrics_collector') and self.azure_metrics_collector:
            self.azure_metrics_collector.stop_continuous_collection()
        
        self.logger.info("Test orchestrator cleanup completed")