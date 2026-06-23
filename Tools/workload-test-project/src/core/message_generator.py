import json
import random
from datetime import datetime, timezone, timedelta
from pathlib import Path
from typing import Dict, Any, List
from src.utils.logger import get_logger


class MessageGenerator:
    """デバイスタイプ別メッセージ生成クラス"""
    
    def __init__(self, template_dir: str = "data/templates"):
        # プロジェクトルートを基準にテンプレートディレクトリを解決
        project_root = Path(__file__).parent.parent.parent
        template_path = Path(template_dir)
        
        if not template_path.is_absolute():
            # 相対パスの場合、プロジェクトルートから探す
            self.template_dir = project_root / template_dir
            if not self.template_dir.exists():
                # カレントディレクトリからも試す
                self.template_dir = Path(template_dir)
        else:
            self.template_dir = template_path
            
        self._templates = {}
        self.logger = get_logger(__name__)
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
                try:
                    with open(template_path, 'r', encoding='utf-8') as f:
                        self._templates[device_type] = json.load(f)
                    self.logger.debug(f"Loaded template for {device_type}")
                except Exception as e:
                    self.logger.warning(f"Failed to load template {filename}: {e}")
            else:
                self.logger.warning(f"Template file not found: {template_path}")

    def generate_bacnet_message(self, device_id: str, point_count: int = 10) -> List[Dict[str, Any]]:
        """BACnetデバイスメッセージ生成
        
        bacnet-device-message.jsonの形式に準拠:
        - BACnetDevice: 3054と3055を使用
        - ObjectTypeとInstanceNoの組み合わせは実際のテストデータに基づく
        """
        current_time = datetime.now(timezone(timedelta(hours=9)))
        
        # BACnetDevice 3054用のObjectType/InstanceNoパターン
        patterns_3054 = [
            # ObjectType 0 (Analog Input) - InstanceNo 1,2,3
            (0, 1), (0, 2), (0, 3),
            # ObjectType 1 (Analog Output) - InstanceNo 1,2,3
            (1, 1), (1, 2), (1, 3),
            # ObjectType 2 (Analog Value) - InstanceNo 1,2,3
            (2, 1), (2, 2), (2, 3),
            # ObjectType 3 (Binary Input) - InstanceNo 1,2,3
            (3, 1), (3, 2), (3, 3),
            # ObjectType 4 (Binary Output) - InstanceNo 1,2,3
            (4, 1), (4, 2), (4, 3),
            # ObjectType 5 (Binary Value) - InstanceNo 1,2,3
            (5, 1), (5, 2), (5, 3),
        ]
        
        # BACnetDevice 3055用のObjectType/InstanceNoパターン
        patterns_3055 = [
            # ObjectType 0 - InstanceNo 101,102,103
            (0, 101), (0, 102), (0, 103),
            # ObjectType 1 - InstanceNo 101,102,103
            (1, 101), (1, 102), (1, 103),
            # ObjectType 2 - InstanceNo 104,105,106
            (2, 104), (2, 105), (2, 106),
            # ObjectType 3 - InstanceNo 101,102,103,111,112,113,114,115,116
            (3, 101), (3, 102), (3, 103), (3, 111), (3, 112), (3, 113), (3, 114), (3, 115), (3, 116),
            # ObjectType 4 - InstanceNo 101,102,103
            (4, 101), (4, 102), (4, 103),
            # ObjectType 5 - InstanceNo 104,105,106
            (5, 104), (5, 105), (5, 106),
            # ObjectType 13 (Multi-state Input) - InstanceNo 101,102,103
            (13, 101), (13, 102), (13, 103),
            # ObjectType 14 (Multi-state Output) - InstanceNo 101,102,103
            (14, 101), (14, 102), (14, 103),
            # ObjectType 19 (Multi-state Value) - InstanceNo 104,105,106
            (19, 104), (19, 105), (19, 106),
            # ObjectType 23 (Large Analog Value) - InstanceNo 201,202,203
            (23, 201), (23, 202), (23, 203),
        ]
        
        # 全パターンを結合
        all_patterns = []
        for obj_type, instance_no in patterns_3054:
            all_patterns.append((3054, obj_type, instance_no))
        for obj_type, instance_no in patterns_3055:
            all_patterns.append((3055, obj_type, instance_no))
        
        # point_countに応じてパターンを選択（循環使用）
        value_strings = []
        for i in range(point_count):
            pattern_index = i % len(all_patterns)
            bacnet_device, object_type, instance_no = all_patterns[pattern_index]
            
            # 値の生成（ObjectTypeに応じて）
            if object_type in [0, 1, 2]:  # Analog values
                present_value = round(random.uniform(0, 100), 1)
            elif object_type in [3, 4, 5]:  # Binary/Digital values  
                present_value = random.randint(0, 1)
            elif object_type in [13, 14, 19]:  # Multi-state values
                present_value = random.randint(1, 3)  # 1-3の範囲
            elif object_type == 23:  # Large Analog values
                present_value = random.randint(1000, 2000)
            else:
                present_value = round(random.uniform(0, 100), 1)
            
            value_strings.append({
                "TimeStamp": current_time.isoformat(),
                "BACnetDevice": bacnet_device,
                "BACnetObject": {
                    "_base": "BACnetObjectIdentifier",
                    "_value": {
                        "ObjectType": object_type,
                        "InstanceNo": instance_no
                    }
                },
                "Properties": {
                    "PresentValue": present_value
                }
            })
            
            # タイムスタンプを少しずつずらす（10-50ミリ秒）
            current_time += timedelta(milliseconds=random.randint(10, 50))
        
        return [{
            "Device_id": "Device1",
            "ValueString": value_strings
        }]

    def generate_hvac_message(self, device_id: str, point_count: int = 10) -> Dict[str, Any]:
        """HVACデバイスメッセージ生成"""
        current_time = datetime.now(timezone(timedelta(hours=9)))
        
        telemetry_data = []
        for i in range(point_count):
            telemetry_data.append({
                "mode": random.choice(["Heat", "Cool", "Auto", "Fan", "Dry"]),
                "fan": random.choice(["Low", "Medium", "High", "Auto"]),
                "setTemp": random.randint(18, 28),
                "onOff": random.choice(["ON", "OFF"]),
                "filterSign": random.randint(0, 1),
                "swing": random.choice(["30deg", "60deg", "90deg", "Auto"]),
                "ambientTemp": round(random.uniform(15, 35), 1),
                "unitName": f"Unit_{i:03d}",
                "unitId": f"{i+1:03d}"
            })
        
        return {
            "telemetryData": telemetry_data,
            "acqTime": current_time.isoformat(),
            "connTime": current_time.isoformat(),
            "deviceId": device_id,
            "ipAddress": f"192.168.{random.randint(1,254)}.{random.randint(1,254)}"
        }

    def generate_environmental_message(self, device_id: str, point_count: int = 10) -> Dict[str, Any]:
        """環境センサーメッセージ生成"""
        current_time = datetime.now(timezone(timedelta(hours=9)))
        
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
                    "temperature": random.randint(1800, 3000),  # 温度は100倍値
                    "humidity": random.randint(3000, 5000)      # 湿度は100倍値
                })
        
        return {
            "logtimestamp": current_time.isoformat(),
            "gateway": f"gateway_{device_id}",
            "sensors": sensors
        }

    def generate_behavior_message(self, device_id: str, point_count: int = 1) -> Dict[str, Any]:
        """行動センサーメッセージ生成"""
        current_time = datetime.now(timezone(timedelta(hours=9)))
        
        # 行動センサーは通常1つのポイントのみ
        return {
            "point_id": device_id,
            "value": random.randint(0, 10),  # 検知人数
            "data": {
                "sbos_space:Name": f"Room_{random.randint(100, 999)}{random.choice(['A', 'B', 'C'])}"
            },
            "datetime": current_time.isoformat(),
            "building": "Engineering(Bldg.2)", 
            "name": f"行動センシングカメラ{device_id[-2:]}",
            "device_id": device_id
        }

    def generate_electric_message(self, device_id: str, point_count: int = 10) -> Dict[str, Any]:
        """電気デバイスメッセージ生成"""
        current_time = datetime.now(timezone(timedelta(hours=9)))
        
        telemetry_data = []
        for i in range(point_count):
            # 電力計測ポイント
            voltage = round(random.uniform(95, 105), 1)      # 電圧 (V)
            current = round(random.uniform(0, 50), 2)        # 電流 (A)  
            power = round(voltage * current, 1)              # 電力 (W)
            energy = round(random.uniform(0, 1000), 2)       # 積算電力量 (kWh)
            
            telemetry_data.append({
                "pointId": f"{device_id}_point_{i:03d}",
                "timestamp": current_time.isoformat(),
                "voltage": voltage,
                "current": current,
                "power": power, 
                "energy": energy,
                "power_factor": round(random.uniform(0.8, 1.0), 2),  # 力率
                "frequency": round(random.uniform(49.5, 50.5), 1)    # 周波数
            })
        
        return {
            "deviceId": device_id,
            "electricTelemetry": telemetry_data,
            "timestamp": current_time.isoformat(),
            "gatewayId": f"gateway_{device_id[:8]}"
        }

    def get_message_template(self, device_type: str) -> Dict[str, Any]:
        """メッセージテンプレート取得"""
        return self._templates.get(device_type, {})

    def estimate_message_size(self, device_type: str, point_count: int = 10) -> int:
        """メッセージサイズ推定（バイト数）"""
        # サンプルメッセージ生成してサイズ計測
        dummy_device_id = f"size_test_{device_type}"
        
        if device_type == "bacnet":
            message = self.generate_bacnet_message(dummy_device_id, point_count)
        elif device_type == "hvac":
            message = self.generate_hvac_message(dummy_device_id, point_count)
        elif device_type == "environmental":
            message = self.generate_environmental_message(dummy_device_id, point_count)
        elif device_type == "electric":
            message = self.generate_electric_message(dummy_device_id, point_count)
        elif device_type == "behavior":
            message = self.generate_behavior_message(dummy_device_id, point_count)
        else:
            return 0
        
        message_json = json.dumps(message, ensure_ascii=False)
        return len(message_json.encode('utf-8'))