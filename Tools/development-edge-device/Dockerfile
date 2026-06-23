# Dockerfile
FROM python:3.9-slim

# 作業ディレクトリの設定
WORKDIR /app

# 必要なパッケージのインストール
RUN pip install --no-cache-dir azure-iot-device

# Pythonスクリプトをコピー
COPY ./iot_edge_device.py .

# スクリプトの実行
CMD ["python", "iot_edge_device.py"]

# requirements.txt (別ファイルとして保存する場合)
# azure-iot-device==2.12.0