#!/usr/bin/env python3
"""
Azure IoT Device (通常版)
EdgeではなくシンプルなIoTデバイスの実装
"""
import asyncio
import json
import logging
import os
from azure.iot.device.aio import IoTHubDeviceClient
from azure.iot.device import MethodResponse

# ログ設定
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

class SimpleIoTDevice:
    def __init__(self, connection_string):
        """
        シンプルなIoTデバイスクラス
        
        Args:
            connection_string (str): IoT Hubから取得した接続文字列
        """
        self.connection_string = connection_string
        self.device_client = None
        
    async def connect(self):
        """IoT Hubへの接続"""
        try:
            self.device_client = IoTHubDeviceClient.create_from_connection_string(
                self.connection_string
            )
            await self.device_client.connect()
            logger.info("IoT Hubに正常に接続しました")
            
            # ダイレクトメソッドハンドラーの設定
            self.device_client.on_method_request_received = self.method_request_handler
            logger.info("ダイレクトメソッドハンドラーを設定しました")
            
        except Exception as e:
            logger.error(f"IoT Hub接続エラー: {e}")
            raise
    
    async def method_request_handler(self, method_request):
        """
        ダイレクトメソッドリクエストのハンドラー
        
        Args:
            method_request: メソッドリクエストオブジェクト
        """
        logger.info(f"ダイレクトメソッド '{method_request.name}' を受信しました")
        logger.info(f"ペイロード: {method_request.payload}")
        
        # すべてのメソッドに対して200を返す
        payload = {
            "result": "success", 
            "message": f"Method '{method_request.name}' executed successfully",
            "timestamp": asyncio.get_event_loop().time()
        }
        status = 200
        
        # メソッドレスポンスを作成
        method_response = MethodResponse.create_from_method_request(
            method_request, status, payload
        )
        
        # レスポンスを送信
        await self.device_client.send_method_response(method_response)
        logger.info(f"ダイレクトメソッド '{method_request.name}' に応答しました (Status: {status})")
    
    async def run(self):
        """メインループ - 接続を維持し続ける"""
        try:
            await self.connect()
            logger.info("IoTデバイスが起動しました。ダイレクトメソッドを待機中...")
            
            # 無限ループで接続を維持
            while True:
                await asyncio.sleep(30)  # 30秒間隔でスリープ
                logger.info("デバイス稼働中... (ダイレクトメソッド待機)")
                
        except KeyboardInterrupt:
            logger.info("ユーザーによって停止されました")
        except Exception as e:
            logger.error(f"実行エラー: {e}")
        finally:
            await self.disconnect()
    
    async def disconnect(self):
        """IoT Hubからの切断"""
        if self.device_client:
            await self.device_client.disconnect()
            logger.info("IoT Hubから切断しました")

async def main():
    """メイン関数"""
    # 接続文字列を環境変数から取得
    connection_string = os.getenv("IOTHUB_DEVICE_CONNECTION_STRING")
    
    if not connection_string:
        logger.error("環境変数 'IOTHUB_DEVICE_CONNECTION_STRING' が設定されていません")
        logger.error("Docker実行時に環境変数を設定してください")
        return
    
    # IoTデバイスを作成・実行
    device = SimpleIoTDevice(connection_string)
    await device.run()

if __name__ == "__main__":
    asyncio.run(main())