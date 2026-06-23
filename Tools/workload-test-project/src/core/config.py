import json
import os
import re
from enum import Enum
from pathlib import Path
from typing import Dict, List, Optional, Any, Union
from pydantic import BaseModel, Field


class DeviceType(str, Enum):
    BACNET = "bacnet"
    HVAC = "hvac"
    ENVIRONMENTAL = "environmental"
    ELECTRIC = "electric"
    BEHAVIOR = "behavior"


class TestStep(BaseModel):
    step_id: int
    step_name: str
    duration_minutes: int


class DeviceScalingStep(TestStep):
    devices_per_type: int


class MessageFrequencyStep(TestStep):
    message_interval_seconds: int


class DataSizeLoadStep(TestStep):
    point_count: int


class AzureConfig(BaseModel):
    iothub_connection_string: str
    device_connection_strings: Dict[str, str]


class Thresholds(BaseModel):
    error_rate_percent: float = 5.0
    timeout_seconds: int = 30
    max_response_time_ms: int = 5000


class BaseTestConfig(BaseModel):
    scenario_type: str
    scenario_name: str
    device_types: List[DeviceType]
    metrics_interval_seconds: int = 30
    azure_config: AzureConfig
    thresholds: Thresholds = Field(default_factory=Thresholds)
    max_concurrent_sends: int = 50  # 同時送信数制限（デフォルト50）


class DeviceScalingConfig(BaseTestConfig):
    scenario_type: str = "device_scaling"
    steps: List[DeviceScalingStep]
    message_interval_seconds: int = 60


class MessageFrequencyConfig(BaseTestConfig):
    scenario_type: str = "message_frequency"
    steps: List[MessageFrequencyStep]
    devices_per_type: int = 200


class DataSizeLoadConfig(BaseTestConfig):
    scenario_type: str = "data_size_load"
    steps: List[DataSizeLoadStep]
    devices_per_type: int = 100
    message_interval_seconds: int = 60


class ConfigManager:
    """設定ファイル管理クラス"""
    
    def __init__(self):
        self._config_cache = {}
    
    @staticmethod
    def substitute_environment_variables(config: Dict[str, Any]) -> Dict[str, Any]:
        """環境変数を設定値に置換"""
        config_str = json.dumps(config)
        
        # ${VAR_NAME} 形式の環境変数を置換
        def replace_env_var(match):
            var_name = match.group(1)
            env_value = os.getenv(var_name)
            if env_value is None:
                raise ValueError(f"Environment variable '{var_name}' is not set")
            return env_value
        
        config_str = re.sub(r'\$\{([^}]+)\}', replace_env_var, config_str)
        return json.loads(config_str)
    
    @staticmethod
    def merge_dicts(base: Dict[str, Any], override: Dict[str, Any]) -> Dict[str, Any]:
        """辞書を再帰的にマージ"""
        result = base.copy()
        
        for key, value in override.items():
            if key in result and isinstance(result[key], dict) and isinstance(value, dict):
                result[key] = ConfigManager.merge_dicts(result[key], value)
            else:
                result[key] = value
        
        return result
    
    def load_config_file(self, config_path: Union[str, Path]) -> Dict[str, Any]:
        """
        設定ファイルを読み込む
        
        Args:
            config_path: 設定ファイルパス
            
        Returns:
            設定辞書
        """
        config_file = Path(config_path)
        if not config_file.is_absolute():
            # プロジェクトルートからの相対パス
            project_root = Path(__file__).parent.parent.parent
            config_file = project_root / config_path
        
        if not config_file.exists():
            raise FileNotFoundError(f"Config file not found: {config_file}")
        
        with open(config_file, 'r', encoding='utf-8') as f:
            return json.load(f)
    
    def load_config(self, scenario: str, custom_config: Optional[str] = None,
                   config_override: Optional[str] = None, device_types: Optional[str] = None,
                   duration: Optional[int] = None) -> Union[DeviceScalingConfig, MessageFrequencyConfig, DataSizeLoadConfig]:
        """設定ファイル読み込み・マージ"""
        
        # プロジェクトルート取得
        project_root = Path(__file__).parent.parent.parent
        
        # デフォルト設定ファイル
        if custom_config:
            config_path = custom_config
        else:
            config_path = f"configs/scenarios/{scenario}.json"
        
        # キャッシュ確認
        cache_key = f"{config_path}:{config_override}:{device_types}:{duration}"
        if cache_key in self._config_cache:
            return self._config_cache[cache_key]
        
        # 設定ファイルパス解決
        config_file_path = Path(config_path)
        if not config_file_path.is_absolute():
            # 相対パスの場合、プロジェクトルートから探す
            config_file_path = project_root / config_path
            if not config_file_path.exists():
                # カレントディレクトリからも試す
                config_file_path = Path(config_path)
        
        if not config_file_path.exists():
            raise FileNotFoundError(f"Config file not found: {config_path} (searched in {project_root} and current directory)")
        
        with open(config_file_path, 'r', encoding='utf-8') as f:
            config_data = json.load(f)
        
        # 環境変数置換
        try:
            config_data = self.substitute_environment_variables(config_data)
        except ValueError as e:
            raise ValueError(f"Environment variable substitution failed: {e}")
        
        # デバイスタイプフィルタ
        if device_types:
            config_data['device_types'] = [dt.strip() for dt in device_types.split(',')]
        
        # 実行時間オーバーライド
        if duration and 'steps' in config_data:
            for step in config_data['steps']:
                step['duration_minutes'] = duration
        
        # JSONオーバーライド適用
        if config_override:
            try:
                override_data = json.loads(config_override)
                config_data = self.merge_dicts(config_data, override_data)
            except json.JSONDecodeError as e:
                raise ValueError(f"Invalid JSON in config override: {e}")
        
        # 設定オブジェクト作成
        scenario_type = config_data.get("scenario_type")
        if scenario_type == "device_scaling":
            config = DeviceScalingConfig(**config_data)
        elif scenario_type == "message_frequency":
            config = MessageFrequencyConfig(**config_data)
        elif scenario_type == "data_size_load":
            config = DataSizeLoadConfig(**config_data)
        else:
            raise ValueError(f"Unknown scenario type: {scenario_type}")
        
        # キャッシュに保存
        self._config_cache[cache_key] = config
        return config
    
    def validate_config(self, config: BaseTestConfig) -> bool:
        """設定値の妥当性チェック"""
        # 基本チェック
        if not config.device_types:
            raise ValueError("At least one device type must be specified")
        
        if not config.azure_config.iothub_connection_string:
            raise ValueError("IoT Hub connection string is required")
        
        # デバイス接続文字列チェック
        for device_type in config.device_types:
            if device_type not in config.azure_config.device_connection_strings:
                raise ValueError(f"Device connection string for '{device_type}' is missing")
        
        # ステップ固有チェック
        if hasattr(config, 'steps') and config.steps:
            step_ids = [step.step_id for step in config.steps]
            if len(step_ids) != len(set(step_ids)):
                raise ValueError("Duplicate step IDs found")
        
        return True