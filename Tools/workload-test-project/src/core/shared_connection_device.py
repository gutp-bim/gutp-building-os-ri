import asyncio
import json
import random
import time
from typing import Dict, Any, Optional, List
from azure.iot.device.aio import IoTHubDeviceClient
from azure.iot.device import MethodResponse
from datetime import datetime, timezone, timedelta
from src.utils.logger import get_logger


class SharedConnectionDevice:
    """
    単一のIoT Hub接続を複数のデバイスIDで共有するクラス
    
    実際には1つのデバイス接続のみを確立し、メッセージペイロードで
    複数のデバイスをエミュレートします
    """
    
    def __init__(self, connection_string: str, primary_device_id: str, 
                 max_concurrent_sends: int = 500):
        """
        Args:
            connection_string: IoT Hubへの接続文字列（1つのデバイスの）
            primary_device_id: プライマリデバイスID
            max_concurrent_sends: 最大同時送信数（デフォルト50）
        """
        self.connection_string = connection_string
        self.primary_device_id = primary_device_id
        self.client: Optional[IoTHubDeviceClient] = None
        self.is_connected = False
        self.logger = get_logger(f"SharedConnection_{primary_device_id}")
        
        # 統計情報（デバイスIDごと）
        self.message_counts: Dict[str, int] = {}
        self.error_counts: Dict[str, int] = {}
        self.total_send_times: Dict[str, float] = {}
        self.throttled_counts: Dict[str, int] = {}  # スロットリングカウント
        self._start_time = None
        self._connection_lock = asyncio.Lock()
        
        # 同時送信数制限用セマフォ
        self.max_concurrent_sends = max_concurrent_sends
        self._send_semaphore = asyncio.Semaphore(max_concurrent_sends)
        self.logger.info(f"Initialized with max_concurrent_sends={max_concurrent_sends}")
        
    async def connect(self) -> bool:
        """IoT Hubへの接続（1回のみ）"""
        async with self._connection_lock:
            if self.is_connected:
                return True
                
            try:
                start_time = time.time()
                self.client = IoTHubDeviceClient.create_from_connection_string(
                    self.connection_string
                )
                await self.client.connect()
                connection_time = time.time() - start_time
                
                self.is_connected = True
                self._start_time = datetime.now()
                self.logger.info(f"Shared connection established (took {connection_time:.2f}s)")
                
                # ダイレクトメソッドハンドラー設定
                self.client.on_method_request_received = self._method_request_handler
                
                return True
                
            except Exception as e:
                self.logger.error(f"Connection failed: {e}")
                return False
    
    async def disconnect(self):
        """IoT Hubからの切断"""
        if self.client and self.is_connected:
            try:
                await self.client.disconnect()
                self.is_connected = False
                self.logger.info("Shared connection disconnected")
            except Exception as e:
                self.logger.error(f"Disconnect error: {e}")
    
    async def _method_request_handler(self, method_request):
        """ダイレクトメソッドリクエストハンドラー"""
        self.logger.debug(f"Received method '{method_request.name}'")
        
        payload = {
            "result": "success",
            "message": f"Method '{method_request.name}' executed successfully",
            "device_id": self.primary_device_id,
            "timestamp": datetime.now(timezone(timedelta(hours=9))).isoformat()
        }
        
        method_response = MethodResponse.create_from_method_request(
            method_request, 200, payload
        )
        
        await self.client.send_method_response(method_response)
    
    async def send_message(self, device_id: str, message_data: Dict[str, Any]) -> bool:
        """
        メッセージ送信（共有接続を使用）
        
        Args:
            device_id: エミュレートするデバイスID
            message_data: メッセージデータ
        
        Returns:
            送信成功の可否
        """
        if not self.is_connected or not self.client:
            self.logger.warning(f"Not connected - cannot send message for {device_id}")
            return False
        
        # 統計情報初期化
        if device_id not in self.message_counts:
            self.message_counts[device_id] = 0
            self.error_counts[device_id] = 0
            self.total_send_times[device_id] = 0.0
            self.throttled_counts[device_id] = 0
        
        # セマフォで同時送信数を制限
        async with self._send_semaphore:
            try:
                start_time = time.time()
                
                # メッセージに送信元デバイスIDを明示的に含める
                message_json = json.dumps(message_data, ensure_ascii=False)
                
                await self.client.send_message(message_json)

                send_time = time.time() - start_time
                self.total_send_times[device_id] += send_time
                self.message_counts[device_id] += 1
                
                self.logger.debug(
                    f"Message sent for emulated device {device_id} "
                    f"(took {send_time:.3f}s, size: {len(message_json)} bytes)"
                )
                return True
                
            except Exception as e:
                self.logger.error(f"Message send failed for {device_id}: {e}")
                self.error_counts[device_id] += 1
                
                # スロットリングエラーの検出
                if "throttled" in str(e).lower() or "429" in str(e):
                    self.throttled_counts[device_id] += 1
                    self.logger.warning(f"Throttled message for {device_id}")
                
                return False
    
    def get_stats(self, device_id: Optional[str] = None) -> Dict[str, Any]:
        """
        統計情報取得
        
        Args:
            device_id: 特定のデバイスIDの統計（Noneの場合は全体）
        
        Returns:
            統計情報
        """
        if device_id:
            return self._get_device_stats(device_id)
        else:
            return self._get_overall_stats()
    
    def _get_device_stats(self, device_id: str) -> Dict[str, Any]:
        """特定デバイスの統計情報"""
        msg_count = self.message_counts.get(device_id, 0)
        err_count = self.error_counts.get(device_id, 0)
        throttled_count = self.throttled_counts.get(device_id, 0)
        total_send_time = self.total_send_times.get(device_id, 0.0)
        
        return {
            "device_id": device_id,
            "messages_sent": msg_count,
            "errors": err_count,
            "throttled": throttled_count,
            "error_rate": err_count / max(msg_count + err_count, 1),
            "average_send_time": total_send_time / max(msg_count, 1) if msg_count > 0 else 0,
            "is_connected": self.is_connected
        }
    
    def _get_overall_stats(self) -> Dict[str, Any]:
        """全体統計情報"""
        total_messages = sum(self.message_counts.values())
        total_errors = sum(self.error_counts.values())
        total_throttled = sum(self.throttled_counts.values())
        total_time = (datetime.now() - self._start_time).total_seconds() if self._start_time else 0
        
        return {
            "primary_device_id": self.primary_device_id,
            "emulated_device_count": len(self.message_counts),
            "total_messages_sent": total_messages,
            "total_errors": total_errors,
            "total_throttled": total_throttled,
            "error_rate": total_errors / max(total_messages + total_errors, 1),
            "throttle_rate": total_throttled / max(total_messages + total_errors, 1),
            "total_runtime_seconds": total_time,
            "max_concurrent_sends": self.max_concurrent_sends,
            "is_connected": self.is_connected,
            "per_device_stats": {
                device_id: self._get_device_stats(device_id)
                for device_id in self.message_counts.keys()
            }
        }


class VirtualDevice:
    """
    共有接続を使用する仮想デバイス
    
    実際の接続は持たず、SharedConnectionDeviceを介してメッセージを送信します
    """
    
    def __init__(self, device_id: str, device_type: str, shared_connection: SharedConnectionDevice):
        """
        Args:
            device_id: 仮想デバイスID
            device_type: デバイスタイプ
            shared_connection: 共有接続インスタンス
        """
        self.device_id = device_id
        self.device_type = device_type
        self.shared_connection = shared_connection
        self.logger = get_logger(f"VirtualDevice_{device_id}")
        self._message_generator = None
        
    def set_message_generator(self, generator_func):
        """
        メッセージ生成関数を設定
        
        Args:
            generator_func: メッセージ生成関数（device_id, point_countを受け取る）
        """
        self._message_generator = generator_func
    
    def generate_message(self, point_count: int = 10) -> Dict[str, Any]:
        """メッセージ生成（サブクラスまたは外部から設定）"""
        if self._message_generator:
            # device_idを含めてメッセージを生成
            return self._message_generator(self.device_id, point_count)
        else:
            raise NotImplementedError("Message generator not set")
    
    async def send_message(self, point_count: int = 1) -> bool:
        """メッセージ送信（共有接続経由）"""
        try:
            message_data = self.generate_message(point_count)
            return await self.shared_connection.send_message(self.device_id, message_data)
        except Exception as e:
            self.logger.error(f"Failed to send message: {e}")
            return False
    
    async def run_continuous(self, interval_seconds: int, point_count: int = 1,
                           duration_minutes: Optional[int] = None, 
                           dry_run: bool = False,
                           initial_delay_max: Optional[float] = None) -> Dict[str, Any]:
        """
        連続メッセージ送信
        
        Args:
            interval_seconds: メッセージ送信間隔（秒）
            point_count: データポイント数
            duration_minutes: 実行時間（分）
            dry_run: ドライラン
            initial_delay_max: 初期遅延の最大値（秒）。Noneの場合は遅延なし
        """
        start_time = datetime.now()
        
        # 初期遅延を追加（メッセージ送信タイミングの分散）
        initial_delay = 0
        if initial_delay_max and initial_delay_max > 0:
            initial_delay = random.uniform(0, initial_delay_max)
            self.logger.info(
                f"Applying initial delay of {initial_delay:.2f}s for {self.device_id} "
                f"to distribute message sending"
            )
            await asyncio.sleep(initial_delay)
        
        self.logger.info(
            f"Starting continuous messaging for {self.device_id} "
            f"(interval: {interval_seconds}s, points: {point_count}, "
            f"duration: {duration_minutes}min, dry_run: {dry_run})"
        )
        
        try:
            while True:
                # 実行時間チェック
                if duration_minutes:
                    elapsed_minutes = (datetime.now() - start_time).total_seconds() / 60
                    if elapsed_minutes >= duration_minutes:
                        self.logger.info(f"Duration limit reached for {self.device_id}")
                        break
                
                # メッセージ送信
                if not dry_run:
                    await self.send_message(point_count)
                else:
                    # ドライランの場合はメッセージ生成のみ
                    self.generate_message(point_count)
                    await asyncio.sleep(0.01)
                
                await asyncio.sleep(interval_seconds)
                
        except asyncio.CancelledError:
            self.logger.info(f"Continuous messaging cancelled for {self.device_id}")
        except Exception as e:
            self.logger.error(f"Continuous messaging error for {self.device_id}: {e}")
        
        return self.shared_connection.get_stats(self.device_id)
    
    async def disconnect(self):
        """切断（実際には何もしない - 共有接続は残す）"""
        # 仮想デバイスなので個別の切断は不要
        pass

