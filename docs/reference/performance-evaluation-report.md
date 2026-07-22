# Building OS OSS パフォーマンス総合評価レポート

最終更新: 2026-07-22
対象: Parquet既定のOSS構成（NATS JetStream / MinIO / OxiGraph / Keycloak / .NET API・ConnectorWorker）

## 1. 結論

ローカル単一ホスト環境で、主要E1〜E8評価に加え、10 Building・20 Gatewayへ決定論的に分散した
**2,000→5,000→10,000→50,000 Point**のスケール評価を完走した。最大構成では以下を確認した。

- 50,000 Point Twinで、Gateway Point Listの最大応答時間は **2,745.0 ms**（予算5,000 ms以内）
- gRPC ingressは **50,000/50,000 frameを受理**し、未知Point **200/200 frameを拒否**
- MinIO Parquetレイクは **50,000/50,000行**、損失率 **0%**
- Parquet可視化まで **20秒**（10秒poll粒度、50,000行flush閾値による早期flush）
- 10,000 Point条件付きPoint Listは、ETag一致時 **0.3929 ms / OxiGraph追加query 0件**
- 100 Gatewayの500 ms集中再接続は **2,693.2 ms** で全接続が制御可能になり、再接続後の
  ingress・controlは各 **100/100成功**、Lake損失・重複および3サービスのerror-level logは0

現在のローカル評価範囲では、50,000 Pointまでデータ整合性と設定済み性能予算を満たす。ただし、50kの
Point Listは2.7秒まで増加しており、100k以上や同時Gateway同期では先に再評価すべき指標である。

## 2. 最新スケールスイープ（#261）

Run: `20260722T045000Z-s17`。各段階は10 Building・20 Gatewayへ均等配置し、段階ごとにTwinを分離した。
各Gatewayは総Pointの1/20を所有する。Point List値は20 Gatewayのうち最も遅い応答である。

| Twin Point | Gateway当たり | Point List最大 | ingress accepted | unknown rejected | Lake行数 | 損失 | Lake可視化 |
|--:|--:|--:|--:|--:|--:|--:|--:|
| 2,000 | 100 | 35.5 ms | 2,000/2,000 | 200/200 | 2,000 | 0% | 60 s |
| 5,000 | 250 | 92.3 ms | 5,000/5,000 | 200/200 | 5,000 | 0% | 60 s |
| 10,000 | 500 | 170.5 ms | 10,000/10,000 | 200/200 | 10,000 | 0% | 60 s |
| 50,000 | 2,500 | 2,745.0 ms | 50,000/50,000 | 200/200 | 50,000 | 0% | 20 s |

ゲートはPoint List ≤5,000 ms、損失率 ≤1%、Lake可視化 ≤120秒。全段階PASS。
Lake可視化時間は書込み処理時間そのものではなく、投入完了からDuckDBで全行を確認できるまでの時間で、
10秒単位に丸められる。2k〜10kは1分flush、50kは行数閾値で先にflushされた。

## 3. Gateway集中再接続（#262）

Run: `20260722T150000Z-s18`。100本の実`GatewayEgress.Connect`双方向gRPC streamを一度切断し、
決定論的な0〜500 ms jitterで同時再接続した。全Gatewayへper-gateway NATS requestで制御を送り、
gRPC `ControlResult`を返した後、実`GatewayIngress`へ正常・未知Pointを各100 frame投入してMinIO Parquetを検査した。

| KPI | 実測 | 閾値 | 判定 |
|---|--:|--:|:--:|
| 再接続・制御可能 | 100/100 | 100/100 | PASS |
| 全Gateway収束 | 2,693.2 ms | ≤10,000 ms | PASS |
| control accepted / succeeded | 100/100 | 100/100 | PASS |
| ingress accepted / unknown rejected | 100/100 | 100/100 | PASS |
| Lake行 / 損失 / 重複 | 100 / 0 / 0 | loss=0, dup=0 | PASS |
| GatewayBridge / ConnectorWorker / API `fail:` | 0 / 0 / 0 | 0 / 0 / 0 | PASS |

収束時間は最初の再接続開始から、全100 GatewayのNATS request ack確認までである。Docker Desktopの
ホストポート転送不調を評価対象から除外するため、負荷プロセスは同一Compose network内から公開サービス境界を
呼び出した。アプリケーションプロトコルやコンテナ構成は変更していない。

## 4. Point List最適化（#259 / #260）

10 Building・10,000 Point、対象Gateway 1,000 Pointの実OxiGraph Testcontainer評価。

| 指標 | 最適化前 | 最適化後 |
|---|--:|--:|
| OxiGraph query | 26,632.6 ms | 91.4 ms |
| API応答 | 未完了（queryのみで予算超過） | 259.8 ms |
| query speedup | — | **291.3倍** |
| ETag一致304 | OxiGraph再検索あり | **0.3929 ms / 追加query 0** |

最初にGateway所有Pointを絞り、属性・Equipment joinを`VALUES`制約することで全Point joinを除去した。
ETagはNATS KVでAPI replica間共有し、Twin Admin更新時はCAS世代更新で失効する。KV障害時は304を信用せず
Twin queryへfail-closedする。

## 5. E1〜E8 ヘッドライン

以下は2026-06のParquet既定構成の再現可能な評価ゲート結果。詳細は
[`e2e/evaluation-report.md`](../../e2e/evaluation-report.md)を参照。

| 軸 | 指標 | 実測 | 閾値 | 判定 |
|---|---|--:|--:|:--:|
| E1 | 持続ingress | 6,000 frame、loss/dup/invalid 0 | ratio ≥0.99 | PASS |
| E2 | ingest E2E p95 | 2.7〜2.9 ms | <2,000 ms | PASS |
| E3 | latest API p95 | 6.9〜51 ms | <500 ms | PASS |
| E3 | event→Hot freshness p95 | 13 ms | <2,000 ms | PASS |
| E4 | warm 24h range p95 | 54.7 ms | <2,000 ms | PASS |
| E4 | cold 7d p95 | 75.4 ms | <5,000 ms | PASS |
| E4 | rollup-backed hourly aggregate p95 | 606 ms | <3,000 ms | PASS |
| E5 | point解決 / unknown・ownership拒否 | 1.000 / 1.000 / 1.000 | ≥0.999 / ==1 | PASS |
| E6 | control RTT p95 / stale replay | 22.8 ms / 0 | <2,000 ms / ==0 | PASS |
| E7 | Parquet bytes/row | 約2.8 B（非圧縮Timescale比約0.02） | ≤0.20 | PASS |
| E8 | ConnectorWorker RTO / 復旧後損失 | 4.52 s / 0% | report / ≤1% | PASS |

補足として、2,005 Point・187分の長時間評価では約356,000 frameを無損失で処理し、HTTPエラー0%、
Point List p95 487.5 ms、Parquet flush p99 194.5 msを記録した。

## 6. 測定環境

| 項目 | 値 |
|---|---|
| OS | WSL2 Linux 6.6.87.2 |
| CPU | AMD Ryzen AI 7 350、8 core / 16 thread |
| Memory | 15 GiB |
| Docker | client/server 29.1.3、Docker Desktop |
| .NET SDK | 8.0.129 |
| Git | `eac3497197695d48ee46d5fbc79a3fb7145acf0e` + #261作業ツリー |
| 構成 | `docker-compose.oss.yaml`, `WARM_STORE=parquet`, `PARQUET_FLUSH_INTERVAL=1` |

絶対値はこの単一ホスト環境に依存する。クラウド/Kubernetesのネットワーク、永続Volume、CPU limit、
同時クライアント数を含む容量保証値ではない。

## 7. ボトルネックと推奨順

1. **Point Listの50k以降**: 10k→50kで170.5→2,745.0 msと非線形に増加した。100k、同時20 Gateway、
   cold cacheで再測定し、必要ならOxiGraph query plan・ページング・事前materializationを検討する。
2. **初回full同期のpayload**: ETag一致304は十分高速だが、Gateway再構築時のfull responseはPoint数に比例する。
   圧縮、差分保持期間、Gateway側適用時間とメモリを次のKPIにする。
3. **Parquet freshness**: 通常は設定した1分flushに支配される。低遅延要件はHot KV/tail-mergeを利用し、
   Lakeのflush間隔を無闇に短縮してsmall-fileを増やさない。
4. **長時間・並行負荷**: 50k評価は各Pointを1回投入する容量・正確性評価であり、50k Pointからの継続同時送信ではない。
   次は50k Twinを維持した1〜4時間のrate sweepと、API read混在負荷を実施する。
5. **専用ベンチ/Kubernetes**: 本番SLO確定前に固定スペックnode、resource limit、永続Volume、複数API replicaで再計測する。

## 8. 再現方法と証跡

```bash
PARQUET_FLUSH_INTERVAL=1 docker compose -f docker-compose.oss.yaml up -d --build
Tools/e2e-performance/.venv/bin/python \
  Tools/e2e-performance/s17_multibuilding_scale_sweep.py \
  --run-id <run-id> --continue-on-failure
```

ランナーは各段階の`topology.json`、`measurements.json`、`kpi.json`と、全体の`kpi-summary.json`、
`report.md`を生成する。最初に失敗した段階は1始まりの終了コードで識別できる。

100 Gateway再接続評価は`Tools/e2e-performance/s18_gateway_reconnect.py`を使用する。実行ごとに一意な
Twin fixtureを作成・清掃し、`kpi-summary.json`、`report.md`、3サービスのログを保存する。

- 最新機械可読KPI: [`Tools/e2e-performance/results/20260722T045000Z-s17/kpi-summary.json`](../../Tools/e2e-performance/results/20260722T045000Z-s17/kpi-summary.json)
- 最新段階レポート: [`Tools/e2e-performance/results/20260722T045000Z-s17/report.md`](../../Tools/e2e-performance/results/20260722T045000Z-s17/report.md)
- #259/#260証跡: [`Tools/e2e-performance/results/20260722T000000Z-gateway-pointlist-259/report.md`](../../Tools/e2e-performance/results/20260722T000000Z-gateway-pointlist-259/report.md)
- #262証跡: [`Tools/e2e-performance/results/20260722T150000Z-s18/report.md`](../../Tools/e2e-performance/results/20260722T150000Z-s18/report.md)

## 9. 評価上の限界

- 50kは10 Building / 20 Gatewayの固定分布で、偏り・Gateway単独50kは未評価。
- ingressは1 stageにつき各Point 1 frame。継続rate、burst、複数同時streamは既存E1/E2とは別条件。
- Point List値は逐次20 Gatewayの最大値で、同時実行時のp95ではない。
- 再接続評価は単一GatewayBridge replica・単一ホストであり、LB、複数replica、TLS終端、WAN jitterは未評価。
- 旧TimescaleDB結果とParquet既定結果を混同しない。本レポートの主結論はParquet既定の実測のみを用いる。
- 初回スモーク失敗値はランナー検証中の認証・起動同期不備であり、正式runには含めていない。
