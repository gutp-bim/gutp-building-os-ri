import json
import time
from datetime import datetime
from pathlib import Path
from threading import Lock
from typing import Dict, Any, Optional, List
from prometheus_client import Counter, Histogram, Gauge, start_http_server
from src.utils.logger import get_logger


class MetricsCollector:
    """メトリクス収集・管理クラス"""
    
    def __init__(self, port: int = 8000, output_dir: str = "./results"):
        self.port = port
        
        # 出力ディレクトリパス解決
        output_path = Path(output_dir)
        if not output_path.is_absolute():
            # 相対パスの場合、プロジェクトルートを基準に解決
            project_root = Path(__file__).parent.parent.parent
            self.output_dir = project_root / output_dir
        else:
            self.output_dir = output_path
            
        self.logger = get_logger(__name__)
        self._metrics_lock = Lock()
        
        # Prometheusメトリクス定義
        self.messages_sent_total = Counter(
            'loadtest_messages_sent_total',
            'Total messages sent successfully',
            ['device_type', 'scenario']
        )
        
        self.messages_failed_total = Counter(
            'loadtest_messages_failed_total',
            'Total messages failed',
            ['device_type', 'scenario', 'error_type']
        )
        
        self.message_send_duration = Histogram(
            'loadtest_message_send_duration_seconds',
            'Message send duration in seconds',
            ['device_type'],
            buckets=[0.01, 0.05, 0.1, 0.5, 1.0, 2.0, 5.0, 10.0, 30.0, float('inf')]
        )
        
        self.active_devices_gauge = Gauge(
            'loadtest_active_devices_by_step',
            'Number of active devices per step',
            ['step_id']
        )
        
        self.device_connection_duration = Histogram(
            'loadtest_device_connection_duration_seconds',
            'Device connection duration in seconds',
            ['device_type'],
            buckets=[0.1, 0.5, 1.0, 2.0, 5.0, 10.0, 30.0, 60.0, float('inf')]
        )
        
        self.active_devices = Gauge(
            'loadtest_active_devices',
            'Number of active devices',
            ['device_type', 'scenario']
        )
        
        self.connected_devices = Gauge(
            'loadtest_connected_devices',
            'Number of connected devices',
            ['device_type']
        )
        
        self.failed_devices = Gauge(
            'loadtest_failed_devices',
            'Number of failed devices',
            ['device_type']
        )
        
        self.device_error_rate = Gauge(
            'loadtest_device_error_rate',
            'Device error rate percentage',
            ['device_type']
        )
        
        self.throughput_messages_per_second = Gauge(
            'loadtest_throughput_messages_per_second',
            'Throughput in messages per second',
            ['device_type', 'scenario']
        )
        
        self.total_data_size_bytes = Counter(
            'loadtest_total_data_size_bytes',
            'Total data size sent in bytes',
            ['device_type']
        )
        
        self.average_message_size_bytes = Gauge(
            'loadtest_average_message_size_bytes',
            'Average message size in bytes',
            ['device_type']
        )
        
        # 内部メトリクス保存
        self.scenario_metrics = {}
        self._http_server_started = False

    def start_metrics_server(self, port: int = 8000):
        """Prometheusメトリクスサーバー起動"""
        if self._http_server_started:
            self.logger.warning("Metrics server already started")
            return
        
        try:
            start_http_server(port)
            self._http_server_started = True
            self.logger.info(f"Prometheus metrics server started on port {port}")
        except Exception as e:
            self.logger.error(f"Failed to start metrics server: {e}")
            raise

    def record_message_sent(self, device_type: str, scenario: str, message_size_bytes: int = 0):
        """メッセージ送信成功記録"""
        with self._metrics_lock:
            self.messages_sent_total.labels(
                device_type=device_type,
                scenario=scenario
            ).inc()
            
            if message_size_bytes > 0:
                self.total_data_size_bytes.labels(device_type=device_type).inc(message_size_bytes)

    def record_message_failed(self, device_type: str, scenario: str, error_type: str):
        """メッセージ送信失敗記録"""
        with self._metrics_lock:
            self.messages_failed_total.labels(
                device_type=device_type,
                scenario=scenario,
                error_type=error_type
            ).inc()

    def record_send_duration(self, device_type: str, duration: float):
        """メッセージ送信時間記録"""
        self.message_send_duration.labels(device_type=device_type).observe(duration)

    def record_connection_duration(self, device_type: str, duration: float):
        """デバイス接続時間記録"""
        self.device_connection_duration.labels(device_type=device_type).observe(duration)

    def set_active_devices(self, device_type: str, scenario: str, count: int):
        """アクティブデバイス数設定"""
        self.active_devices.labels(
            device_type=device_type,
            scenario=scenario
        ).set(count)

    def set_connected_devices(self, device_type: str, count: int):
        """接続済みデバイス数設定"""
        self.connected_devices.labels(device_type=device_type).set(count)

    def set_failed_devices(self, device_type: str, count: int):
        """失敗デバイス数設定"""
        self.failed_devices.labels(device_type=device_type).set(count)

    def set_device_error_rate(self, device_type: str, error_rate_percent: float):
        """デバイスエラー率設定"""
        self.device_error_rate.labels(device_type=device_type).set(error_rate_percent)

    def set_throughput(self, device_type: str, scenario: str, messages_per_second: float):
        """スループット設定"""
        self.throughput_messages_per_second.labels(
            device_type=device_type,
            scenario=scenario
        ).set(messages_per_second)

    def set_average_message_size(self, device_type: str, size_bytes: int):
        """平均メッセージサイズ設定"""
        self.average_message_size_bytes.labels(device_type=device_type).set(size_bytes)

    def save_step_metrics(self, scenario_name: str, step_id: int, step_name: str, 
                         metrics: Dict[str, Any]):
        """ステップメトリクス保存"""
        with self._metrics_lock:
            if scenario_name not in self.scenario_metrics:
                self.scenario_metrics[scenario_name] = {}
            
            self.scenario_metrics[scenario_name][f"step_{step_id}"] = {
                "step_id": step_id,
                "step_name": step_name,
                "timestamp": datetime.now().isoformat(),
                **metrics
            }
            
        self.logger.debug(f"Saved metrics for {scenario_name} step {step_id}")

    def calculate_step_summary(self, device_stats: List[Dict[str, Any]]) -> Dict[str, Any]:
        """ステップサマリー計算"""
        if not device_stats:
            return {}
        
        total_messages = sum(stats['messages_sent'] for stats in device_stats)
        total_errors = sum(stats['errors'] for stats in device_stats)
        total_devices = len(device_stats)
        connected_devices = sum(1 for stats in device_stats if stats['is_connected'])
        
        # エラー率計算
        error_rate = (total_errors / max(total_messages + total_errors, 1)) * 100
        
        # 平均送信時間計算
        send_times = [stats['average_send_time'] for stats in device_stats if stats['average_send_time'] > 0]
        avg_send_time = sum(send_times) / len(send_times) if send_times else 0
        
        # スループット計算（全デバイス合計）
        total_runtime = max(stats['total_runtime_seconds'] for stats in device_stats) if device_stats else 0
        throughput = total_messages / max(total_runtime, 1) if total_runtime > 0 else 0
        
        # デバイスタイプ別集計
        device_type_breakdown = {}
        for stats in device_stats:
            device_type = stats['device_type']
            if device_type not in device_type_breakdown:
                device_type_breakdown[device_type] = {
                    'devices': 0,
                    'messages': 0,
                    'errors': 0,
                    'error_rate': 0,
                    'avg_send_time': 0
                }
            
            breakdown = device_type_breakdown[device_type]
            breakdown['devices'] += 1
            breakdown['messages'] += stats['messages_sent']
            breakdown['errors'] += stats['errors']
        
        # デバイスタイプ別エラー率計算
        for device_type, breakdown in device_type_breakdown.items():
            total_ops = breakdown['messages'] + breakdown['errors']
            breakdown['error_rate'] = (breakdown['errors'] / max(total_ops, 1)) * 100
            
            # デバイスタイプ別平均送信時間
            type_send_times = [stats['average_send_time'] for stats in device_stats 
                             if stats['device_type'] == device_type and stats['average_send_time'] > 0]
            breakdown['avg_send_time'] = sum(type_send_times) / len(type_send_times) if type_send_times else 0
        
        return {
            "total_devices": total_devices,
            "connected_devices": connected_devices,
            "messages_sent": total_messages,
            "messages_failed": total_errors,
            "error_rate_percent": round(error_rate, 3),
            "average_send_time_ms": round(avg_send_time * 1000, 2),
            "throughput_msgs_per_sec": round(throughput, 2),
            "total_runtime_seconds": round(total_runtime, 1),
            "device_type_breakdown": device_type_breakdown
        }

    def export_metrics(self, output_path: str):
        """メトリクスをJSONファイルにエクスポート"""
        # 出力パス解決
        output_file = Path(output_path)
        if not output_file.is_absolute():
            # 相対パスの場合、プロジェクトルートを基準に解決
            project_root = Path(__file__).parent.parent.parent
            output_file = project_root / output_path
            
        output_file.parent.mkdir(parents=True, exist_ok=True)
        
        # 現在時刻をレポートに追加
        export_data = {
            "export_timestamp": datetime.now().isoformat(),
            "scenarios": self.scenario_metrics
        }
        
        with open(output_file, 'w', encoding='utf-8') as f:
            json.dump(export_data, f, ensure_ascii=False, indent=2)
        
        self.logger.info(f"Metrics exported to {output_path}")

    def get_current_metrics_summary(self) -> Dict[str, Any]:
        """現在のメトリクスサマリー取得"""
        with self._metrics_lock:
            return {
                "timestamp": datetime.now().isoformat(),
                "total_scenarios": len(self.scenario_metrics),
                "scenarios": list(self.scenario_metrics.keys())
            }

    async def start(self):
        """メトリクス収集開始"""
        try:
            start_http_server(self.port)
            self.logger.info(f"Prometheus metrics server started on port {self.port}")
        except Exception as e:
            self.logger.error(f"Failed to start metrics server: {e}")

    async def stop(self):
        """メトリクス収集停止"""
        self.logger.info("Metrics collector stopped")

    def get_output_file(self) -> str:
        """出力ファイルパス取得"""
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        return str(self.output_dir / f"metrics_{timestamp}.json")

    async def record_active_devices(self, step_id: int, count: int):
        """アクティブデバイス数記録"""
        self.active_devices_gauge.labels(step_id=str(step_id)).set(count)
        self.logger.debug(f"Active devices for step {step_id}: {count}")

    async def record_step_completion(self, step_id: int, metrics: Dict[str, Any]):
        """ステップ完了記録"""
        self.logger.info(f"Step {step_id} metrics recorded: {metrics}")
        # 必要に応じて具体的なメトリクス処理を追加