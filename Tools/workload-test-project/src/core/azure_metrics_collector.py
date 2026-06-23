"""
Azure Monitor メトリクス収集モジュール

Azure Functions, CosmosDB, IoT Hub のメトリクスを Azure Monitor API から収集します。
"""

import asyncio
import logging
from datetime import datetime, timedelta, timezone
from typing import List, Optional
from dataclasses import dataclass
from pathlib import Path

from azure.mgmt.monitor import MonitorManagementClient
from azure.identity import DefaultAzureCredential, EnvironmentCredential
from azure.core.exceptions import AzureError

import pandas as pd
import matplotlib.pyplot as plt
import seaborn as sns


logger = logging.getLogger(__name__)


@dataclass
class MetricDefinition:
    """メトリクス定義"""
    name: str
    display_name: str
    metric_name: str  # Azure Monitor での名前
    aggregation: str  # 'Average', 'Total', 'Maximum', 'Minimum'
    unit: str = ""
    description: str = ""


@dataclass
class AzureResourceConfig:
    """Azure リソース設定"""
    resource_id: str
    resource_type: str  # 'function', 'cosmosdb', 'iothub'
    resource_name: str


@dataclass
class MetricDataPoint:
    """メトリクスデータポイント"""
    timestamp: datetime
    resource_type: str
    resource_name: str
    metric_name: str
    value: float
    unit: str = ""
    step_id: Optional[int] = None
    step_name: Optional[str] = None


class AzureMetricsCollector:
    """Azure Monitor メトリクス収集クラス"""

    # メトリクス定義
    FUNCTION_METRICS = [
        MetricDefinition(
            name="function_execution_time",
            display_name="実行時間（平均）",
            metric_name="HttpResponseTime",
            aggregation="Average",
            unit="ms",
            description="Azure Functions の平均実行時間"
        ),
        MetricDefinition(
            name="function_execution_count",
            display_name="実行回数",
            metric_name="FunctionExecutionCount",
            aggregation="Total",
            unit="count",
            description="Azure Functions の実行回数"
        ),
        MetricDefinition(
            name="function_errors",
            display_name="エラー数",
            metric_name="Http5xx",
            aggregation="Total",
            unit="count",
            description="HTTP 5xx エラー数"
        ),
        MetricDefinition(
            name="function_instances",
            display_name="同時実行数",
            metric_name="InstanceCount",
            aggregation="Average",
            unit="count",
            description="アクティブインスタンス数"
        ),
        MetricDefinition(
            name="function_memory",
            display_name="メモリ使用量",
            metric_name="AverageMemoryWorkingSet",
            aggregation="Average",
            unit="B",
            description="平均メモリ使用量"
        )
    ]

    COSMOSDB_METRICS = [
        MetricDefinition(
            name="cosmosdb_total_request_units",
            display_name="RU消費量",
            metric_name="TotalRequestUnits",
            aggregation="Total",
            unit="RU",
            description="リクエストユニット総消費量"
        ),
        MetricDefinition(
            name="cosmosdb_server_latency",
            display_name="応答時間（サーバー）",
            metric_name="ServerSideLatency",
            aggregation="Average",
            unit="ms",
            description="サーバー側レイテンシー"
        ),
        MetricDefinition(
            name="cosmosdb_total_requests",
            display_name="総リクエスト数",
            metric_name="TotalRequests",
            aggregation="Total",
            unit="count",
            description="総リクエスト数"
        )
    ]

    IOTHUB_METRICS = [
        MetricDefinition(
            name="iothub_telemetry_messages",
            display_name="受信テレメトリメッセージ数",
            metric_name="d2c.telemetry.ingress.success",
            aggregation="Total",
            unit="count",
            description="デバイスから受信したテレメトリメッセージ数"
        ),
        MetricDefinition(
            name="iothub_routing_delivery_latency",
            display_name="ルーティング配信遅延",
            metric_name="d2c.endpoints.latency.builtIn.events",
            aggregation="Average",
            unit="ms",
            description="メッセージ受信からイベントハブへの配信までの遅延"
        ),
    ]

    def __init__(
        self,
        subscription_id: str,
        function_resource_ids: Optional[List[str]] = None,
        cosmosdb_resource_ids: Optional[List[str]] = None,
        iothub_resource_ids: Optional[List[str]] = None,
        use_environment_credential: bool = False
    ):
        """
        Args:
            subscription_id: Azure サブスクリプション ID
            function_resource_ids: Azure Functions リソース ID リスト
            cosmosdb_resource_ids: CosmosDB リソース ID リスト
            iothub_resource_ids: IoT Hub リソース ID リスト
            use_environment_credential: 環境変数から認証情報を取得するかどうか
        """
        self.subscription_id = subscription_id

        # 認証設定
        if use_environment_credential:
            self.credential = EnvironmentCredential()
        else:
            self.credential = DefaultAzureCredential()

        # MonitorManagementClient を初期化
        self.monitor_client = MonitorManagementClient(
            credential=self.credential,
            subscription_id=subscription_id
        )

        # リソース設定
        self.resources: List[AzureResourceConfig] = []
        
        if function_resource_ids:
            for rid in function_resource_ids:
                self.resources.append(AzureResourceConfig(
                    resource_id=rid,
                    resource_type="function",
                    resource_name=self._extract_resource_name(rid)
                ))

        if cosmosdb_resource_ids:
            for rid in cosmosdb_resource_ids:
                self.resources.append(AzureResourceConfig(
                    resource_id=rid,
                    resource_type="cosmosdb",
                    resource_name=self._extract_resource_name(rid)
                ))

        if iothub_resource_ids:
            for rid in iothub_resource_ids:
                self.resources.append(AzureResourceConfig(
                    resource_id=rid,
                    resource_type="iothub",
                    resource_name=self._extract_resource_name(rid)
                ))

        # メトリクスデータ保存
        self.metric_data_points: List[MetricDataPoint] = []
        self.collection_running = False

    @staticmethod
    def _extract_resource_name(resource_id: str) -> str:
        """リソースIDからリソース名を抽出"""
        try:
            return resource_id.split('/')[-1]
        except Exception:
            return "unknown"

    def _get_metrics_for_resource_type(self, resource_type: str) -> List[MetricDefinition]:
        """リソースタイプに対応するメトリクス定義を取得"""
        if resource_type == "function":
            return self.FUNCTION_METRICS
        elif resource_type == "cosmosdb":
            return self.COSMOSDB_METRICS
        elif resource_type == "iothub":
            return self.IOTHUB_METRICS
        else:
            return []

    async def collect_metrics_once(
        self,
        start_time: datetime,
        end_time: datetime,
        step_id: Optional[int] = None,
        step_name: Optional[str] = None
    ) -> List[MetricDataPoint]:
        """
        指定期間のメトリクスを1回収集

        Args:
            start_time: 収集開始時刻
            end_time: 収集終了時刻
            step_id: テストステップID
            step_name: テストステップ名

        Returns:
            収集したメトリクスデータポイントのリスト
        """
        collected_points = []

        for resource in self.resources:
            metrics_definitions = self._get_metrics_for_resource_type(resource.resource_type)

            for metric_def in metrics_definitions:
                try:
                    logger.debug(
                        f"Collecting {metric_def.display_name} for {resource.resource_name} "
                        f"({start_time} - {end_time})"
                    )

                    # タイムスパンをISO 8601形式の文字列に変換
                    # マイクロ秒を削除し、UTCタイムゾーンを'Z'で表現
                    start_str = start_time.replace(microsecond=0).isoformat().replace('+00:00', 'Z')
                    end_str = end_time.replace(microsecond=0).isoformat().replace('+00:00', 'Z')
                    
                    # タイムゾーンオフセットがない場合は'Z'を追加
                    if not start_str.endswith('Z') and not start_str.endswith('+00:00'):
                        start_str += 'Z'
                    if not end_str.endswith('Z') and not end_str.endswith('+00:00'):
                        end_str += 'Z'
                    
                    timespan = f"{start_str}/{end_str}"
                    
                    logger.debug(f"Timespan: {timespan}")
                    
                    # メトリクスを取得
                    response = self.monitor_client.metrics.list(
                        resource_uri=resource.resource_id,
                        timespan=timespan,
                        interval='PT1M',  # 1分間隔
                        metricnames=metric_def.metric_name,
                        aggregation=metric_def.aggregation
                    )

                    # レスポンスからメトリクスデータを取得
                    for metric in response.value:
                        for time_series in metric.timeseries:
                            for data_point in time_series.data:
                                if data_point.time_stamp:
                                    # 集約タイプに応じた値を取得
                                    value = None
                                    if metric_def.aggregation.lower() == 'average':
                                        value = data_point.average
                                    elif metric_def.aggregation.lower() == 'total':
                                        value = data_point.total
                                    elif metric_def.aggregation.lower() == 'maximum':
                                        value = data_point.maximum
                                    elif metric_def.aggregation.lower() == 'minimum':
                                        value = data_point.minimum

                                    if value is not None:
                                        point = MetricDataPoint(
                                            timestamp=data_point.time_stamp,
                                            resource_type=resource.resource_type,
                                            resource_name=resource.resource_name,
                                            metric_name=metric_def.name,
                                            value=value,
                                            unit=metric_def.unit,
                                            step_id=step_id,
                                            step_name=step_name
                                        )
                                        collected_points.append(point)
                                        self.metric_data_points.append(point)

                except AzureError as e:
                    logger.error(
                        f"Failed to collect metric {metric_def.name} for {resource.resource_name}: {e}"
                    )
                except Exception as e:
                    logger.error(
                        f"Unexpected error collecting metric {metric_def.name}: {e}",
                        exc_info=True
                    )

        logger.info(f"Collected {len(collected_points)} data points")
        return collected_points

    async def start_continuous_collection(
        self,
        interval_seconds: int = 60,
        step_id: Optional[int] = None,
        step_name: Optional[str] = None
    ):
        """
        継続的なメトリクス収集を開始

        Args:
            interval_seconds: 収集間隔（秒）
            step_id: テストステップID
            step_name: テストステップ名
        """
        self.collection_running = True
        logger.info(f"Starting continuous metrics collection (interval: {interval_seconds}s)")

        while self.collection_running:
            try:
                end_time = datetime.now(timezone.utc)
                start_time = end_time - timedelta(seconds=interval_seconds)

                await self.collect_metrics_once(
                    start_time=start_time,
                    end_time=end_time,
                    step_id=step_id,
                    step_name=step_name
                )

                await asyncio.sleep(interval_seconds)

            except Exception as e:
                logger.error(f"Error during continuous collection: {e}", exc_info=True)
                await asyncio.sleep(interval_seconds)

    def stop_continuous_collection(self):
        """継続的なメトリクス収集を停止"""
        self.collection_running = False
        logger.info("Stopped continuous metrics collection")

    def export_to_csv(self, output_path: str):
        """
        収集したメトリクスを CSV ファイルにエクスポート

        Args:
            output_path: 出力ファイルパス
        """
        if not self.metric_data_points:
            logger.warning("No metrics data to export")
            return

        # DataFrame に変換
        df = pd.DataFrame([
            {
                'timestamp': point.timestamp.isoformat(),
                'resource_type': point.resource_type,
                'resource_name': point.resource_name,
                'metric_name': point.metric_name,
                'value': point.value,
                'unit': point.unit,
                'step_id': point.step_id,
                'step_name': point.step_name
            }
            for point in self.metric_data_points
        ])

        # CSV として保存
        output_file = Path(output_path)
        output_file.parent.mkdir(parents=True, exist_ok=True)
        
        df.to_csv(output_file, index=False, encoding='utf-8-sig')
        logger.info(f"Exported metrics to CSV: {output_file}")

        return df

    def generate_summary_by_step(self) -> pd.DataFrame:
        """
        ステップごとのメトリクスサマリーを生成

        Returns:
            ステップごとの統計情報を含む DataFrame
        """
        if not self.metric_data_points:
            logger.warning("No metrics data for summary")
            return pd.DataFrame()

        # DataFrame に変換
        df = pd.DataFrame([
            {
                'timestamp': point.timestamp,
                'resource_type': point.resource_type,
                'resource_name': point.resource_name,
                'metric_name': point.metric_name,
                'value': point.value,
                'unit': point.unit,
                'step_id': point.step_id,
                'step_name': point.step_name
            }
            for point in self.metric_data_points
        ])

        # ステップごとに集計
        summary = df.groupby(['step_id', 'step_name', 'resource_type', 'metric_name']).agg({
            'value': ['mean', 'min', 'max', 'std', 'count', 'sum']
        }).reset_index()

        summary.columns = ['step_id', 'step_name', 'resource_type', 'metric_name', 
                          'avg', 'min', 'max', 'std', 'count', 'sum']

        return summary

    def export_summary_to_csv(self, output_path: str):
        """
        ステップごとのサマリーを CSV ファイルにエクスポート

        Args:
            output_path: 出力ファイルパス
        """
        summary = self.generate_summary_by_step()
        
        if summary.empty:
            logger.warning("No summary data to export")
            return

        output_file = Path(output_path)
        output_file.parent.mkdir(parents=True, exist_ok=True)
        
        summary.to_csv(output_file, index=False, encoding='utf-8-sig')
        logger.info(f"Exported summary to CSV: {output_file}")

        return summary

    def generate_graphs(self, output_dir: str):
        """
        メトリクスのグラフを生成

        Args:
            output_dir: グラフの出力ディレクトリ
        """
        if not self.metric_data_points:
            logger.warning("No metrics data for graphs")
            return

        output_path = Path(output_dir)
        output_path.mkdir(parents=True, exist_ok=True)

        # DataFrame に変換
        df = pd.DataFrame([
            {
                'timestamp': point.timestamp,
                'resource_type': point.resource_type,
                'resource_name': point.resource_name,
                'metric_name': point.metric_name,
                'value': point.value,
                'unit': point.unit,
                'step_id': point.step_id,
                'step_name': point.step_name
            }
            for point in self.metric_data_points
        ])

        # スタイル設定
        sns.set_style("whitegrid")
        plt.rcParams['figure.figsize'] = (14, 8)
        plt.rcParams['font.sans-serif'] = ['MS Gothic', 'Yu Gothic', 'Meiryo', 'DejaVu Sans']
        plt.rcParams['axes.unicode_minus'] = False

        # リソースタイプごとにグラフを生成
        resource_types = df['resource_type'].unique()

        for resource_type in resource_types:
            resource_df = df[df['resource_type'] == resource_type]
            metrics = resource_df['metric_name'].unique()

            # メトリクスごとにグラフ生成
            for metric_name in metrics:
                metric_df = resource_df[resource_df['metric_name'] == metric_name]
                
                if metric_df.empty:
                    continue

                # 時系列グラフ
                self._plot_time_series(
                    metric_df, 
                    resource_type, 
                    metric_name, 
                    output_path
                )

                # ステップ別比較グラフ
                if 'step_name' in metric_df.columns and metric_df['step_name'].notna().any():
                    self._plot_by_step(
                        metric_df, 
                        resource_type, 
                        metric_name, 
                        output_path
                    )

        logger.info(f"Generated graphs in: {output_path}")

    def _plot_time_series(
        self, 
        df: pd.DataFrame, 
        resource_type: str, 
        metric_name: str, 
        output_path: Path
    ):
        """時系列グラフを作成"""
        try:
            fig, ax = plt.subplots(figsize=(14, 8))

            for resource_name in df['resource_name'].unique():
                resource_df = df[df['resource_name'] == resource_name]
                ax.plot(
                    resource_df['timestamp'], 
                    resource_df['value'],
                    marker='o',
                    label=resource_name,
                    linewidth=2,
                    markersize=4
                )

            # ステップ境界を表示
            if 'step_name' in df.columns and df['step_name'].notna().any():
                step_changes = df.groupby('step_id')['timestamp'].min()
                for step_id, timestamp in step_changes.items():
                    ax.axvline(x=timestamp, color='red', linestyle='--', alpha=0.5)
                    step_name = df[df['step_id'] == step_id]['step_name'].iloc[0]
                    ax.text(timestamp, ax.get_ylim()[1] * 0.95, 
                           f'Step {step_id}: {step_name}',
                           rotation=90, verticalalignment='top')

            unit = df['unit'].iloc[0] if 'unit' in df.columns else ""
            ax.set_xlabel('時刻', fontsize=12)
            ax.set_ylabel(f'値 ({unit})' if unit else '値', fontsize=12)
            ax.set_title(f'{resource_type.upper()} - {metric_name} (時系列)', fontsize=14, fontweight='bold')
            ax.legend(loc='best')
            ax.grid(True, alpha=0.3)
            plt.xticks(rotation=45, ha='right')
            plt.tight_layout()

            filename = f"{resource_type}_{metric_name}_timeseries.png"
            plt.savefig(output_path / filename, dpi=150, bbox_inches='tight')
            plt.close()

            logger.debug(f"Generated time series graph: {filename}")

        except Exception as e:
            logger.error(f"Failed to plot time series for {metric_name}: {e}", exc_info=True)

    def _plot_by_step(
        self, 
        df: pd.DataFrame, 
        resource_type: str, 
        metric_name: str, 
        output_path: Path
    ):
        """ステップ別比較グラフを作成"""
        try:
            # ステップごとの平均値を計算
            step_summary = df.groupby(['step_name', 'resource_name'])['value'].mean().reset_index()

            fig, ax = plt.subplots(figsize=(12, 6))

            # 棒グラフ
            resource_names = step_summary['resource_name'].unique()
            step_names = step_summary['step_name'].unique()
            
            x = range(len(step_names))
            width = 0.8 / len(resource_names)

            for i, resource_name in enumerate(resource_names):
                resource_data = step_summary[step_summary['resource_name'] == resource_name]
                values = [
                    resource_data[resource_data['step_name'] == step]['value'].mean() 
                    if not resource_data[resource_data['step_name'] == step].empty 
                    else 0
                    for step in step_names
                ]
                
                ax.bar(
                    [xi + i * width for xi in x],
                    values,
                    width,
                    label=resource_name
                )

            unit = df['unit'].iloc[0] if 'unit' in df.columns else ""
            ax.set_xlabel('テストステップ', fontsize=12)
            ax.set_ylabel(f'平均値 ({unit})' if unit else '平均値', fontsize=12)
            ax.set_title(f'{resource_type.upper()} - {metric_name} (ステップ別比較)', 
                        fontsize=14, fontweight='bold')
            ax.set_xticks([xi + width * (len(resource_names) - 1) / 2 for xi in x])
            ax.set_xticklabels(step_names, rotation=45, ha='right')
            ax.legend(loc='best')
            ax.grid(True, alpha=0.3, axis='y')
            plt.tight_layout()

            filename = f"{resource_type}_{metric_name}_by_step.png"
            plt.savefig(output_path / filename, dpi=150, bbox_inches='tight')
            plt.close()

            logger.debug(f"Generated step comparison graph: {filename}")

        except Exception as e:
            logger.error(f"Failed to plot by step for {metric_name}: {e}", exc_info=True)

    def get_all_metrics_as_dataframe(self) -> pd.DataFrame:
        """全メトリクスを DataFrame として取得"""
        if not self.metric_data_points:
            return pd.DataFrame()

        return pd.DataFrame([
            {
                'timestamp': point.timestamp,
                'resource_type': point.resource_type,
                'resource_name': point.resource_name,
                'metric_name': point.metric_name,
                'value': point.value,
                'unit': point.unit,
                'step_id': point.step_id,
                'step_name': point.step_name
            }
            for point in self.metric_data_points
        ])

