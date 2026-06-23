@echo off
REM Azure EntraID認証プロキシサーバー起動スクリプト (Windows)

setlocal enabledelayedexpansion

cd /d "%~dp0"

echo ============================================================
echo Azure EntraID認証プロキシサーバー 起動
echo ============================================================
echo.

REM .envファイルの存在確認
if not exist ".env" (
    echo ❌ エラー: .envファイルが見つかりません
    echo.
    echo 📝 以下のコマンドで.envファイルを作成してください：
    echo    copy env.template .env
    echo.
    echo その後、.envファイルを編集して必要な情報を入力してください。
    exit /b 1
)

REM Pythonの確認
python --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ エラー: Pythonが見つかりません
    echo Python 3.8以上をインストールしてください。
    exit /b 1
)

echo ✅ 環境チェック完了
echo.

REM 依存関係のインストール確認
echo 📦 依存関係を確認しています...
python -c "import flask, requests, dotenv" >nul 2>&1
if %errorlevel% neq 0 (
    echo ⚠️  依存関係が不足しています。インストールします...
    python -m pip install -r requirements.txt
    echo ✅ 依存関係のインストール完了
) else (
    echo ✅ 依存関係は既にインストールされています
)

echo.
echo 🚀 プロキシサーバーを起動します...
echo.

REM プロキシサーバー起動
python proxy_server.py

