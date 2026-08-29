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
   100kでの初回計測と、その結果分かったことは§7.1を参照。
2. **初回full同期のpayload**: ETag一致304は十分高速だが、Gateway再構築時のfull responseはPoint数に比例する。
   圧縮、差分保持期間、Gateway側適用時間とメモリを次のKPIにする。
3. **Parquet freshness**: 通常は設定した1分flushに支配される。低遅延要件はHot KV/tail-mergeを利用し、
   Lakeのflush間隔を無闇に短縮してsmall-fileを増やさない。
4. **長時間・並行負荷**: 50k評価は各Pointを1回投入する容量・正確性評価であり、50k Pointからの継続同時送信ではない。
   次は50k Twinを維持した1〜4時間のrate sweepと、API read混在負荷を実施する。
5. **専用ベンチ/Kubernetes**: 本番SLO確定前に固定スペックnode、resource limit、永続Volume、複数API replicaで再計測する。

### 7.1 100k profiling results（診断計測、非公式ベンチマーク）

Point List projection計画（Phase A）の一環として、`GatewayPointListScaleTest`に100k（10 Building ×
10,000 Point/Gateway）ケースと20 Gateway同時アクセスケースを追加し、`ListGatewayPointList`が発行する
3クエリ（point-URI解決／属性VALUES制約クエリ／device VALUES制約クエリ）ごとの時間内訳、JSON
シリアライズ単体の時間、cold（初回）/warmの区別を計測できるようにした。あわせて
`s17_scale_stage.py`/`s17_multibuilding_scale_sweep.py`にも同種の診断計測（warm再計測・同時実行
p50/p95/max、規模別閾値`--point-list-ms-by-scale`）を追加した。

**⚠️ 以下の絶対値は§6の測定環境（固定スペック専用ホスト）ではなく、この作業を行った共有サンドボックス
環境での一回限りの実行値であり、10kケース自体も§4の91.4msに対し実測546msと桁が異なる（環境間の
比較用途には使えない）。ここで意味を持つのは絶対値ではなく、内訳の比率が示す相対的な知見である。**

100kケース（1 Gatewayが10,000 Point）の実測（cold）:

| 指標 | 値 |
|---|--:|
| API全体（`apiResponseMilliseconds`） | 47,379 ms |
| うちOxiGraph HTTPクエリ合計（3クエリ） | 4,327 ms |
| — point-URI解決 | 2,519 ms |
| — 属性（VALUES制約） | 426 ms |
| — device（VALUES制約） | 1,382 ms |
| JSONシリアライズ単体 | 28 ms |
| ETag一致304 | 0.3 ms、追加OxiGraphクエリ0 |

warm（直後の再計測）もcoldとほぼ同値（`apiResponseMilliseconds` 47,360 ms、OxiGraph 4,215 ms）——
コールドキャッシュ由来の揺らぎではなく、恒常的な内訳だと分かる。

**分かったこと**: `apiResponseMilliseconds`（47.4秒）のうちOxiGraphへのHTTP往復が占めるのは
4.3秒（約9%）に過ぎず、残り約9割はこの3クエリの外で消費されている。10kケースではAPI全体966msに
対しOxiGraphクエリ546ms（約57%）と比率が大きく異なり、規模とともにこの「クエリ外」の割合が
増えている。250k（`BUILDINGOS_SCALE_STRETCH=1`でopt-in実行）はこの作業では未実行。

### 7.2 GC/CPUプロファイリング結果 — 「.NET側処理説」は裏付けられなかった

上記の「クエリ外の9割」について、当初は`attributesByPoint`/`devicesByPoint`の`GroupBy`/`ToDictionary`
構築など.NET側のLINQ/GC処理が支配的ではないかと仮説を立てたが、`dotnet-counters`（`System.Runtime`
カウンタ）を100kケースの実行中にアタッチして直接観測した結果、**この仮説は裏付けられなかった**。

- **CPU使用率は最大でも3.6%**。43秒規模のCPUバウンドな処理（LINQ/Dictionary構築）が起きていれば
  相応の期間高いCPU使用率が観測されるはずだが、そのような区間は見られなかった。
- **GCはほぼ発生していない**: 34秒間`Gen 0/1/2 GC Count`が0のまま推移し、その後短いバースト
  （数秒間でGen0数回・Gen1/2各1回、GC一時停止は最大でも11ms/秒程度）があるのみ。大規模な
  アロケーション処理を示す継続的なGC活動は観測されなかった。
- **スレッドプールのキュー滞留（`ThreadPool Queue Length`）は常に0、ロック競合
  （`Monitor Lock Contention Count`）も0**。スレッドプール枯渇やロック待ちでもない。
- 観測された挙動は、CPU・GC・スレッドプールのいずれの指標から見てもプロセスがほぼ**アイドル
  （何かを待っている状態）**であることを示しており、いずれのリソースにも負荷がかかっていない。

**結論**: 「クエリ外の9割」がどこで消費されているかは、このプロファイリングだけでは特定できな
かった。少なくとも.NET側のCPUバウンドな処理（LINQ/GC/JSON構築）が主要因という当初の仮説は誤りで
あり、取り下げる。低CPU・低GCで長時間かかる挙動はネットワーク/IO待ちに典型的だが、
`QueryTimingHandler`で計測した3クエリのHTTP往復時間（4.3秒）にその待ち時間が含まれていない理由は
不明のままである。この共有サンドボックス環境（Docker Desktop、他プロセスと共有）自体が測定を
歪めている可能性もあり、§6の専用ホストで`dotnet-trace`によるフル（CPU + 待機時間の呼び出しスタック
付き）トレースを取得しない限り、真因の特定はできない。

20 Gateway同時アクセス（50k Point、10k Pointのケースと同じ共有サンドボックスで実行）:
wall-clock 39.7秒、p50 38.3秒、p95 39.6秒、max 39.7秒——同時実行数の増加で1リクエストあたりの
レイテンシがさらに悪化する傾向も確認できたが、これも同一の環境要因が乗っている可能性があり、
専用ホストでの再計測が必要。

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
- Point List値は逐次20 Gatewayの最大値で、同時実行時のp95ではない（`s17_scale_stage.py`に
  同時実行p50/p95/max計測を追加済みだが、§6の専用ホストではまだ実行していない — §7.1参照）。
- §7.1の100k/20同時Gateway診断値は共有サンドボックス環境の一回限りの実行であり、絶対値は§6の
  専用ホストでの計測と比較できない。相対的な内訳の知見（OxiGraphクエリ時間が全体の一部に過ぎない
  こと）のみを暫定的な仮説として扱う。
- 再接続評価は単一GatewayBridge replica・単一ホストであり、LB、複数replica、TLS終端、WAN jitterは未評価。
- 旧TimescaleDB結果とParquet既定結果を混同しない。本レポートの主結論はParquet既定の実測のみを用いる。
- 初回スモーク失敗値はランナー検証中の認証・起動同期不備であり、正式runには含めていない。
