# E6 — Control path 安全性

## 目的
Telemetry は Store-and-Forward、Control は deadline-bounded な request-reply とし、stale command を
replay しない設計により、履歴データの耐障害性と物理設備制御の安全性を両立できることを示す。

## 経路
API → NATS `building-os.control.request`（in-proc: NatsPointControlWorker）/
API → `building-os.control.request.gw.{id}` → GatewayBridge → BOWS。結果は
`building-os.control.result.{controlId}`。offline gateway は NATS no-responders → **503**
（`control.requests{result=gateway_offline}`）。

## 計測指標
- command success rate（正常時）。
- command round-trip latency p50/p95/p99（API→gateway→connector→result）。
- typed failure ratio: timeout / no_connector / not_writable / device_error の分類率。
- **stale replay count**（障害復旧後に古い command が実行された件数）。
- duplicate write count（同一 control_id の二重 write）。
- not-writable rejection count（writable=false への制御拒否）。
- offline→503 ratio / command timeout ratio。

## シナリオ
1. **normal**: 制御成功・round-trip 計測。
2. **connector down**: no_connector / offline→503 を確認。
3. **not writable**: writable=false の point → 拒否（admin でもブロック、CLAUDE.md）。
4. **duplicate control_id**: 二重 write が起きないこと。
5. **outage→recovery**: 障害中に出した古い command が復旧後に replay されない（stale replay = 0）。

## 合否（kpi-thresholds.yaml: E6_control_safety）
success ≥ 99% / RTT p95 < 2s / **stale replay = 0** / duplicate write = 0 / not-writable・offline→503
100% / typed failure 分類 100%。

## 既存資産・ギャップ
- 既存: `s6_point_control.sh`, `k6/s6_point_control.js`。
- **ギャップ**: stale replay / duplicate control_id / offline→503 / typed failure の体系的シナリオと集計。
