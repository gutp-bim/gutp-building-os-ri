import logging
import sys
from pathlib import Path
from typing import Optional


class ColoredFormatter(logging.Formatter):
    """カラー付きログフォーマッター"""
    
    COLORS = {
        'DEBUG': '\033[36m',     # シアン
        'INFO': '\033[32m',      # 緑
        'WARNING': '\033[33m',   # 黄
        'ERROR': '\033[31m',     # 赤
        'CRITICAL': '\033[35m',  # マゼンタ
        'RESET': '\033[0m'       # リセット
    }
    
    def format(self, record):
        log_color = self.COLORS.get(record.levelname, self.COLORS['RESET'])
        record.levelname = f"{log_color}{record.levelname}{self.COLORS['RESET']}"
        return super().format(record)


def setup_logging(log_level: str = "INFO", debug: bool = False, 
                 log_file: Optional[str] = None) -> None:
    """ログ設定初期化"""
    
    # ログレベル設定
    level = getattr(logging, log_level.upper(), logging.INFO)
    
    # ログフォーマット
    console_format = "%(asctime)s [%(levelname)s] %(name)s: %(message)s"
    file_format = "%(asctime)s [%(levelname)s] %(name)s - %(filename)s:%(lineno)d: %(message)s"
    
    # ルートロガー設定
    logger = logging.getLogger()
    logger.setLevel(level)
    
    # 既存ハンドラーをクリア
    for handler in logger.handlers[:]:
        logger.removeHandler(handler)
    
    # コンソールハンドラー
    console_handler = logging.StreamHandler(sys.stdout)
    console_handler.setLevel(level)
    
    if debug:
        console_formatter = ColoredFormatter(console_format)
    else:
        console_formatter = logging.Formatter(console_format)
    
    console_handler.setFormatter(console_formatter)
    logger.addHandler(console_handler)
    
    # ファイルハンドラー
    if log_file:
        log_path = Path(log_file)
        
        # 相対パスの場合、プロジェクトルートを基準に解決
        if not log_path.is_absolute():
            project_root = Path(__file__).parent.parent.parent
            log_path = project_root / log_file
        
        log_path.parent.mkdir(parents=True, exist_ok=True)
        
        file_handler = logging.FileHandler(log_path, encoding='utf-8')
        file_handler.setLevel(level)
        file_formatter = logging.Formatter(file_format)
        file_handler.setFormatter(file_formatter)
        logger.addHandler(file_handler)
    
    # Azure SDK のログレベル調整
    azure_loggers = [
        'azure.iot.device',
        'azure.core.pipeline.transport',
        'azure.core.pipeline.policies.http_logging_policy'
    ]
    
    for azure_logger_name in azure_loggers:
        azure_logger = logging.getLogger(azure_logger_name)
        azure_logger.setLevel(logging.WARNING if not debug else logging.DEBUG)


def get_logger(name: str) -> logging.Logger:
    """ロガー取得"""
    return logging.getLogger(name)