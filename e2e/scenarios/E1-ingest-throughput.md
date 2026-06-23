# E1 — Telemetry ingest 性能

## 目的
実ビル規模の時系列を、欠損・重複・検証エラーを抑えつつ実用的なスループットで取り込めることを示す。

## 経路・構成
正本: gateway → **gRPC GatewayIngress**（ConnectorWorker, `GRPC_INGRESS_PORT`）→ `building-os.validated.telemetry`
→ ParquetLakeWriter → MinIO。対照: MQTT(Mosquitto)→`raw.mqtt`→MqttConnectorWorker。

## 負荷
スケール・マトリクス（plan.md §2）の small/medium/large/stress/burst。各 run = warmup→本計測。

## 計測指標
- ingest throughput: frames/s, points/s, messages/s
- accepted/sent ratio（gateway 送信に対する受理割合）
- loss rate（生成数に対する欠損）/ duplicate rate / validation error rate
- NATS JetStream pending / backlog（`nats stream info`、`*_pending` メトリクス）
- 資源使用量: ConnectorWorker / NATS / gateway の CPU・memory（cAdvisor or `docker stats`）

## 手順
1. `docker compose -f docker-compose.oss.yaml up -d`
2. OxiGraph に point list を seed（OxiGraphSeed）。gateway_id/point_id を負荷生成器と共有。
3. 負荷生成（ギャップ: gRPC ingress クライアント。暫定は `device_load_generator.py` の MQTT 経路）。
4. `s2_baseline.sh`（throughput）/ `s3_burst.sh`（burst 吸収・backpressure）。
5. 生成数・受理数・重複・検証エラーを `quality_checker.py` で突合。

## 合否（kpi-thresholds.yaml: E1_ingest_throughput）
sustained throughput ≥ 99% / loss ≤1% / duplicate ≤0.5% / validation error ≤1%。

## 既存資産・ギャップ
- 既存: `s2_baseline.sh`, `s3_burst.sh`, `device_load_generator.py`, `quality_checker.py`。
- **ギャップ**: gRPC GatewayIngress 負荷クライアント（proto `gateway_ingress.proto`、client-stream
  `TelemetryFrame`）。点 ID 共有・所有検証込みの正本経路を測るために必要。
