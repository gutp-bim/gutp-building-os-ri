#!/bin/bash
# Azure EntraID認証プロキシサーバー起動スクリプト (Linux/Mac/MSYS2)

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "============================================================"
echo "Azure EntraID認証プロキシサーバー 起動"
echo "============================================================"

# .envファイルの存在確認
if [ ! -f ".env" ]; then
    echo "❌ エラー: .envファイルが見つかりません"
    echo ""
    echo "📝 以下のコマンドで.envファイルを作成してください："
    echo "   cp env.template .env"
    echo ""
    echo "その後、.envファイルを編集して必要な情報を入力してください。"
    exit 1
fi

# Pythonの確認
if ! command -v python3 &> /dev/null && ! command -v python &> /dev/null; then
    echo "❌ エラー: Pythonが見つかりません"
    echo "Python 3.8以上をインストールしてください。"
    exit 1
fi

# Pythonコマンドの決定
PYTHON_CMD="python3"
if ! command -v python3 &> /dev/null; then
    PYTHON_CMD="python"
fi

echo "✅ 環境チェック完了"
echo ""

# 依存関係のインストール確認
echo "📦 依存関係を確認しています..."
if ! $PYTHON_CMD -c "import flask, requests, dotenv" 2>/dev/null; then
    echo "⚠️  依存関係が不足しています。インストールします..."
    $PYTHON_CMD -m pip install -r requirements.txt
    echo "✅ 依存関係のインストール完了"
else
    echo "✅ 依存関係は既にインストールされています"
fi

echo ""
echo "🚀 プロキシサーバーを起動します..."
echo ""

# プロキシサーバー起動
$PYTHON_CMD proxy_server.py

