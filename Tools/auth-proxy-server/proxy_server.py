from flask import Flask, request, jsonify
import requests
import msal
import os
from dotenv import load_dotenv
from urllib.parse import quote

load_dotenv()

app = Flask(__name__)

# MSAL設定
MSAL_CONFIG = {
    "client_id": os.getenv("CLIENT_ID"),
    "authority": f"https://login.microsoftonline.com/{os.getenv('TENANT_ID')}",
    "client_credential": os.getenv("CLIENT_SECRET"),
}

API_BASE_URL = os.getenv("API_BASE_URL")
API_SCOPE = f"api://{os.getenv('API_CLIENT_ID')}/.default"

# MSALクライアント初期化
msal_app = msal.ConfidentialClientApplication(
    MSAL_CONFIG["client_id"],
    authority=MSAL_CONFIG["authority"],
    client_credential=MSAL_CONFIG["client_credential"],
)


def get_access_token():
    """アクセストークンを取得"""
    result = msal_app.acquire_token_for_client(scopes=[API_SCOPE])

    if "access_token" in result:
        return result["access_token"]
    else:
        error_msg = result.get("error_description", "Unknown error")
        raise Exception(f"Failed to acquire token: {error_msg}")


@app.route('/<path:path>', methods=['GET', 'POST', 'PUT', 'DELETE', 'PATCH'])
def proxy(path):
    """すべてのAPIリクエストをプロキシ"""
    try:
        # アクセストークン取得
        token = get_access_token()

        # バックエンドAPIのURL構築（pathをURLエンコード）
        encoded_path = quote(path, safe='/')
        api_url = f"{API_BASE_URL}/{encoded_path}"

        # ヘッダー設定
        headers = {
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
        }

        # クエリパラメータを配列対応で処理
        # request.args.lists()で同じキーの複数値を正しく取得
        params = []
        for key, values in request.args.lists():
            for value in values:
                params.append((key, value))

        # リクエスト転送（paramsは自動的にURLエンコードされる）
        response = requests.request(
            method=request.method,
            url=api_url,
            headers=headers,
            json=request.get_json() if request.is_json else None,
            params=params,
        )

        # レスポンス返却
        return jsonify(response.json() if response.content else {}), response.status_code

    except Exception as e:
        print(f"Proxy error: {str(e)}")
        return jsonify({"error": str(e)}), 500


@app.route('/health', methods=['GET'])
def health():
    """ヘルスチェックエンドポイント"""
    return jsonify({"status": "ok"}), 200


if __name__ == '__main__':
    print("Proxy server starting on http://localhost:5000")
    app.run(host='0.0.0.0', port=5000, debug=True)