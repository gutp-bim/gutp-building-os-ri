from typing import Dict, Any, List, Optional, Callable
from src.core.config import DeviceType
from src.core.message_generator import MessageGenerator
from src.core.shared_connection_device import SharedConnectionDevice, VirtualDevice
from src.utils.logger import get_logger


def _get_message_generator(device_type: DeviceType) -> Callable[[str, int], Dict[str, Any]]:
    """
    デバイスタイプに応じたメッセージジェネレーター関数を取得

    Args:
        device_type: デバイスタイプ

    Returns:
        メッセージ生成関数（device_id, point_countを受け取り、メッセージを返す）
    """
    message_gen = MessageGenerator()

    # デバイスタイプに応じたメッセージ生成関数を返す
    if device_type == DeviceType.BACNET:
        return lambda device_id, point_count=10: message_gen.generate_bacnet_message(device_id, point_count)
    elif device_type == DeviceType.HVAC:
        return lambda device_id, point_count=10: message_gen.generate_hvac_message(device_id, point_count)
    elif device_type == DeviceType.ENVIRONMENTAL:
        return lambda device_id, point_count=10: message_gen.generate_environmental_message(device_id, point_count)
    elif device_type == DeviceType.ELECTRIC:
        return lambda device_id, point_count=10: message_gen.generate_electric_message(device_id, point_count)
    elif device_type == DeviceType.BEHAVIOR:
        return lambda device_id, point_count=10: message_gen.generate_behavior_message(device_id, point_count)
    else:
        raise ValueError(f"Unsupported device type: {device_type}")


class DeviceFactory:
    """デバイスファクトリークラス"""
    
    def __init__(self, device_connection_strings: Dict[str, str],
                 max_concurrent_sends: int = 50):
        self.device_connection_strings = device_connection_strings
        self.max_concurrent_sends = max_concurrent_sends
        self.logger = get_logger(__name__)

    def create_virtual_devices_batch(self, device_type: DeviceType, count: int,
                                   shared_connection: SharedConnectionDevice,
                                   id_prefix: Optional[str] = None) -> List[VirtualDevice]:
        """
        共有接続を使用する仮想デバイスを一括作成
        
        Args:
            device_type: デバイスタイプ
            count: 作成するデバイス数
            shared_connection: 共有接続インスタンス
            id_prefix: デバイスIDプレフィックス
        
        Returns:
            仮想デバイスのリスト
        """
        devices = []
        prefix = id_prefix or f"loadtest-{device_type}"
        
        # デバイスタイプに応じたメッセージジェネレーターを取得
        message_generator = _get_message_generator(device_type)
        
        for i in range(count):
            device_id = f"{prefix}-{i:04d}"
            try:
                virtual_device = VirtualDevice(device_id, device_type, shared_connection)
                virtual_device.set_message_generator(message_generator)
                devices.append(virtual_device)
            except Exception as e:
                self.logger.error(f"Failed to create virtual device {device_id}: {e}")
        
        self.logger.info(f"Created {len(devices)} virtual {device_type} devices")
        return devices

    def create_shared_connection(self, device_type: DeviceType,
                               primary_device_id: str) -> SharedConnectionDevice:
        """
        共有接続インスタンス作成
        
        Args:
            device_type: デバイスタイプ
            primary_device_id: プライマリデバイスID
        
        Returns:
            共有接続インスタンス
        """
        connection_string_template = self.device_connection_strings.get(device_type)
        if not connection_string_template:
            raise ValueError(f"Connection string for device type '{device_type}' not found")
        
        # プライマリデバイスIDで接続文字列作成
        connection_string = connection_string_template.format(id=primary_device_id)
        
        return SharedConnectionDevice(
            connection_string, 
            primary_device_id,
            max_concurrent_sends=self.max_concurrent_sends
        )

    def validate_device_connections(self) -> Dict[str, bool]:
        """デバイス接続文字列の妥当性チェック"""
        validation_results = {}
        
        for device_type, connection_template in self.device_connection_strings.items():
            try:
                # テスト用デバイスIDで接続文字列をフォーマット
                test_connection = connection_template.format(id="test-validation")
                
                # 基本的な接続文字列形式チェック
                required_parts = ["HostName=", "DeviceId=", "SharedAccessKey="]
                is_valid = all(part in test_connection for part in required_parts)
                
                validation_results[device_type] = is_valid
                
                if not is_valid:
                    self.logger.error(f"Invalid connection string format for {device_type}")
                
            except Exception as e:
                self.logger.error(f"Connection string validation failed for {device_type}: {e}")
                validation_results[device_type] = False
        
        return validation_results