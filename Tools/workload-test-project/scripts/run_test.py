#!/usr/bin/env python3
"""
Building OS 負荷試験実行ツール
"""
import asyncio
import click
import json
import os
import sys
from pathlib import Path
from typing import List, Optional, Dict, Any

# プロジェクトルートをPythonパスに追加
project_root = Path(__file__).parent.parent
sys.path.insert(0, str(project_root))

from src.core.config import ConfigManager, BaseTestConfig
from src.core.test_orchestrator import TestOrchestrator
from src.utils.logger import setup_logging, get_logger


@click.command()
@click.option('--scenario',
              type=click.Choice(['device_scaling', 'message_frequency', 'data_size_load']),
              required=True,
              help='実行するテストシナリオ')
@click.option('--step',
              default='all',
              help='実行するステップ (all, 1, 2,3 または 1,3,4 等)')
@click.option('--device-types',
              default=None,
              help='対象デバイスタイプ (bacnet,hvac,environmental,electric,behavior)')
@click.option('--config',
              default=None,
              help='カスタム設定ファイルパス')
@click.option('--config-override',
              default=None,
              help='設定オーバーライド (JSON文字列)')
@click.option('--duration',
              type=int,
              default=None,
              help='各ステップの実行時間（分）')
@click.option('--dry-run',
              is_flag=True,
              help='ドライランモード（実際のメッセージ送信なし）')
@click.option('--debug',
              is_flag=True,
              help='デバッグモード')
@click.option('--log-level',
              type=click.Choice(['DEBUG', 'INFO', 'WARNING', 'ERROR']),
              default='INFO',
              help='ログレベル')
@click.option('--output-dir',
              default='./results',
              help='結果出力ディレクトリ')
@click.option('--metrics-port',
              type=int,
              default=8000,
              help='Prometheusメトリクスサーバーポート')
@click.option('--log-file',
              default=None,
              help='ログファイルパス')
def main(scenario: str, step: str, device_types: Optional[str], config: Optional[str],
         config_override: Optional[str], duration: Optional[int], dry_run: bool,
         debug: bool, log_level: str, output_dir: str, metrics_port: int,
         log_file: Optional[str]):
    """Building OS 負荷試験実行ツール
    
    Examples:
        python run_test.py --scenario device_scaling --step all
        python run_test.py --scenario message_frequency --step 1,3 --device-types bacnet,hvac
        python run_test.py --scenario data_size_load --step 2 --duration 15 --debug
    """
    
    # 環境変数ファイル読み込み
    load_env_file()
    
    # ログ設定
    if not log_file and not dry_run:
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        log_file = f"logs/loadtest_{scenario}_{timestamp}.log"
    
    setup_logging(log_level, debug, log_file)
    logger = get_logger(__name__)
    
    try:
        # 設定読み込み・マージ
        config_manager = ConfigManager()
        test_config = config_manager.load_config(
            scenario, config, config_override, device_types, duration
        )
        
        # 設定バリデーション
        config_manager.validate_config(test_config)
        
        # 実行ステップ解析
        target_steps = parse_steps(step, test_config)
        
        logger.info(f"Starting load test: {test_config.scenario_name}")
        logger.info(f"Target steps: {target_steps}")
        logger.info(f"Device types: {test_config.device_types}")
        logger.info(f"Dry run: {dry_run}")
        
        # テスト実行
        orchestrator = TestOrchestrator(test_config, output_dir, metrics_port, dry_run)
        asyncio.run(orchestrator.execute_steps(target_steps))
        
    except KeyboardInterrupt:
        logger.info("Test interrupted by user")
        sys.exit(1)
    # except Exception as e:
    #     logger.error(f"Test execution failed: {e}")
    #     if debug:
    #         import traceback
    #         traceback.print_exc()
    #     sys.exit(1)


def load_env_file():
    """環境変数ファイル読み込み"""
    # プロジェクトルートから.envを探す
    env_file = project_root / '.env'
    if not env_file.exists():
        # カレントディレクトリも試す
        env_file = Path('.env')
    
    if env_file.exists():
        try:
            from dotenv import load_dotenv
            load_dotenv(env_file)
        except ImportError:
            # dotenvが利用できない場合は手動読み込み
            with open(env_file, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line and not line.startswith('#') and '=' in line:
                        key, value = line.split('=', 1)
                        os.environ[key.strip()] = value.strip().strip('"\'')
    else:
        # .envファイルが見つからない場合の警告
        import warnings
        warnings.warn(f".env file not found in {project_root} or current directory")


def parse_steps(step_arg: str, config: BaseTestConfig) -> List[int]:
    """ステップ引数を解析"""
    if step_arg.lower() == 'all':
        return [s.step_id for s in config.steps]
    
    try:
        steps = []
        for s in step_arg.split(','):
            step_num = int(s.strip())
            steps.append(step_num)
        
        # 有効なステップIDかチェック
        valid_step_ids = [s.step_id for s in config.steps]
        invalid_steps = [s for s in steps if s not in valid_step_ids]
        if invalid_steps:
            raise click.BadParameter(f"Invalid step IDs: {invalid_steps}. Valid steps: {valid_step_ids}")
        
        return steps
        
    except ValueError:
        raise click.BadParameter(f"Invalid step format: '{step_arg}'. Use 'all' or comma-separated numbers like '1,2,3'")


if __name__ == "__main__":
    from datetime import datetime
    main()