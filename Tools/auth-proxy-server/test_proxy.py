#!/usr/bin/env python3
"""
プロキシサーバーのテストスクリプト

プロキシサーバーが正常に動作しているか確認するための簡易テストツール
"""

import requests
import sys
from typing import Optional

# カラー出力用のANSIエスケープコード
class Colors:
    GREEN = '\033[92m'
    RED = '\033[91m'
    YELLOW = '\033[93m'
    BLUE = '\033[94m'
    RESET = '\033[0m'


def print_success(message: str):
    """成功メッセージを出力"""
    print(f"{Colors.GREEN}✅ {message}{Colors.RESET}")


def print_error(message: str):
    """エラーメッセージを出力"""
    print(f"{Colors.RED}❌ {message}{Colors.RESET}")


def print_info(message: str):
    """情報メッセージを出力"""
    print(f"{Colors.BLUE}ℹ️  {message}{Colors.RESET}")


def print_warning(message: str):
    """警告メッセージを出力"""
    print(f"{Colors.YELLOW}⚠️  {message}{Colors.RESET}")


def test_health_endpoint(proxy_url: str) -> bool:
    """
    ヘルスチェックエンドポイントをテスト
    
    Args:
        proxy_url: プロキシサーバーのURL
    
    Returns:
        bool: テスト成功/失敗
    """
    print_info("ヘルスチェックエンドポイントをテスト中...")
    
    try:
        response = requests.get(f"{proxy_url}/health", timeout=10)
        
        if response.status_code == 200:
            print_success(f"ヘルスチェック成功 (Status: {response.status_code})")
            print(f"レスポンス: {response.json()}")
            return True
        else:
            print_error(f"ヘルスチェック失敗 (Status: {response.status_code})")
            print(f"レスポンス: {response.text}")
            return False
            
    except requests.exceptions.ConnectionError:
        print_error("プロキシサーバーに接続できません。サーバーが起動しているか確認してください。")
        return False
    except requests.exceptions.Timeout:
        print_error("タイムアウトしました。")
        return False
    except Exception as e:
        print_error(f"予期しないエラー: {e}")
        return False


def test_buildings_endpoint(proxy_url: str) -> bool:
    """
    Building一覧エンドポイントをテスト
    
    Args:
        proxy_url: プロキシサーバーのURL
    
    Returns:
        bool: テスト成功/失敗
    """
    print_info("Building一覧エンドポイントをテスト中...")
    
    try:
        response = requests.get(f"{proxy_url}/buildings", timeout=10)
        
        if response.status_code == 200:
            print_success(f"Building一覧取得成功 (Status: {response.status_code})")
            data = response.json()
            print(f"取得件数: {len(data) if isinstance(data, list) else 'N/A'}")
            return True
        elif response.status_code == 401:
            print_error("認証エラー (Status: 401)")
            print("認証トークンが無効または期限切れの可能性があります。")
            return False
        else:
            print_warning(f"予期しないステータスコード: {response.status_code}")
            print(f"レスポンス: {response.text[:500]}")
            return False
            
    except requests.exceptions.ConnectionError:
        print_error("プロキシサーバーに接続できません。")
        return False
    except requests.exceptions.Timeout:
        print_error("タイムアウトしました。")
        return False
    except Exception as e:
        print_error(f"予期しないエラー: {e}")
        return False


def test_custom_endpoint(proxy_url: str, endpoint: str) -> bool:
    """
    カスタムエンドポイントをテスト
    
    Args:
        proxy_url: プロキシサーバーのURL
        endpoint: テストするエンドポイント（例: /buildings/building-001）
    
    Returns:
        bool: テスト成功/失敗
    """
    print_info(f"カスタムエンドポイントをテスト中: {endpoint}")
    
    try:
        response = requests.get(f"{proxy_url}{endpoint}", timeout=10)
        
        print(f"Status Code: {response.status_code}")
        
        if response.status_code in [200, 201, 204]:
            print_success("リクエスト成功")
            if response.text:
                print(f"レスポンス: {response.text[:500]}")
            return True
        elif response.status_code == 401:
            print_error("認証エラー (Status: 401)")
            return False
        elif response.status_code == 404:
            print_warning("エンドポイントが見つかりません (Status: 404)")
            return False
        else:
            print_warning(f"ステータスコード: {response.status_code}")
            print(f"レスポンス: {response.text[:500]}")
            return True  # エラーではないのでTrueを返す
            
    except requests.exceptions.ConnectionError:
        print_error("プロキシサーバーに接続できません。")
        return False
    except requests.exceptions.Timeout:
        print_error("タイムアウトしました。")
        return False
    except Exception as e:
        print_error(f"予期しないエラー: {e}")
        return False


def main():
    """メイン関数"""
    print("=" * 60)
    print("Azure EntraID認証プロキシサーバー テストツール")
    print("=" * 60)
    print()
    
    # プロキシURLの設定
    proxy_url = "http://localhost:8080"
    
    # コマンドライン引数でプロキシURLを指定可能
    if len(sys.argv) > 1:
        proxy_url = sys.argv[1].rstrip('/')
    
    print_info(f"プロキシURL: {proxy_url}")
    print()
    
    # テスト実行
    results = []
    
    # 1. ヘルスチェックエンドポイントのテスト
    results.append(("ヘルスチェック", test_health_endpoint(proxy_url)))
    print()
    
    # 2. Building一覧エンドポイントのテスト
    results.append(("Building一覧", test_buildings_endpoint(proxy_url)))
    print()
    
    # 3. カスタムエンドポイントのテスト（オプション）
    if len(sys.argv) > 2:
        custom_endpoint = sys.argv[2]
        results.append((f"カスタム ({custom_endpoint})", test_custom_endpoint(proxy_url, custom_endpoint)))
        print()
    
    # 結果サマリー
    print("=" * 60)
    print("テスト結果サマリー")
    print("=" * 60)
    
    success_count = sum(1 for _, result in results if result)
    total_count = len(results)
    
    for test_name, result in results:
        status = "✅ 成功" if result else "❌ 失敗"
        print(f"{test_name}: {status}")
    
    print()
    print(f"成功: {success_count}/{total_count}")
    
    # 全テスト成功の場合
    if success_count == total_count:
        print()
        print_success("すべてのテストが成功しました！")
        print_info("プロキシサーバーは正常に動作しています。")
        return 0
    else:
        print()
        print_warning("一部のテストが失敗しました。")
        print_info("プロキシサーバーまたはバックエンドAPIの設定を確認してください。")
        return 1


if __name__ == "__main__":
    sys.exit(main())

