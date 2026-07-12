# E2E 共通 fixture（Building OS ⇔ nexus-gateway）

簡易的な E2E 実証で **Building OS** と **`../nexus-gateway`**（+ BACnet/OPC-UA シミュレータ）が
共通で使う **1 セットのポイントリスト / リソース定義**です。両者が同じ
`(gateway_id, point_id)` を指すことで、上り（テレメトリ）と下り（制御）が端から端まで繋がります。

ポイントリストの列定義は **スマートビル標準ポイントリスト**（`pointlist.md`）に準拠します。

## データセット概要

- **gateway_id:** `GW-SOS-001`（1 gateway = 1 building 制約を満たす）
- **building:** `bldg-e2e` / **floor:** `floor-1` / **room:** `Room 101`
- **device:** `DEV-ENV-1`（センサー） / `DEV-AHU-1`（制御対象）
- **point:** `SOS-PT-001` .. `SOS-PT-008`

| point_id | 名称 | BACnet (object,instance) | local_id (OPC-UA nodeId) | unit | writable | dataType |
|---|---|---|---|---|---|---|
| SOS-PT-001 | Room Temperature | analogInput,1001 | ns=2;s=SOS-PT-001 | degC | no | number |
| SOS-PT-002 | Room Humidity | analogInput,1002 | ns=2;s=SOS-PT-002 | % | no | number |
| SOS-PT-003 | CO2 Concentration | analogInput,1003 | ns=2;s=SOS-PT-003 | ppm | no | number |
| SOS-PT-004 | Lighting On/Off | binaryOutput,2001 | ns=2;s=SOS-PT-004 | – | **yes** | boolean |
| SOS-PT-005 | Occupancy | binaryInput,2002 | ns=2;s=SOS-PT-005 | – | no | boolean |
| SOS-PT-006 | Setpoint Temperature | analogValue,1002 | ns=2;s=SOS-PT-006 | degC | **yes** | number (16–30) |
| SOS-PT-007 | Fan Speed | multiStateValue,3001 | ns=2;s=SOS-PT-007 | – | **yes** | enum (Off/Low/Medium/High) |
| SOS-PT-008 | Active Power | analogInput,1004 | ns=2;s=SOS-PT-008 | kW | no | number |

## ファイル

| ファイル | 用途 | 消費側 |
|---|---|---|
| [`twin.ttl`](twin.ttl) | Building OS デジタルツイン シード（SBCO Turtle）。**正本**。 | Building OS（OxiGraph） |
| [`pointlist.json`](pointlist.json) | `GET /gateways/GW-SOS-001/pointlist` が返す JSON と等価な参照スナップショット | 参照 / gateway 同期の期待値 |
| [`pointlist.csv`](pointlist.csv) | スマートビル標準ポイントリスト形式のフラット CSV。gateway の bootstrap / シミュレータ生成用 | nexus-gateway / bbc-sim / opcua-sim |

> **正本は `twin.ttl`（Building OS の twin）です。** gateway は `GET /gateways/{id}/pointlist`
> でここから同期するのが本来の経路で、`pointlist.json` はその期待値、`pointlist.csv` は
> ファイル起動（`PROVISIONING_FILE`）やシミュレータ生成のためのフラット表現です。3 つは
> 同じデータセットを表します。

> **local_id と BACnet / OPC-UA:** `local_id`（twin では `sbco:localId`）は設備側ポイント識別子
> （標準ポイントリストの local_id：BACnet なら ObjectID、MQTT なら TOPIC、OPC-UA なら nodeId）です。
> 本 fixture では **BACnet のネイティブアドレスを `device_id_bacnet` / `object_type_bacnet` /
> `instance_no_bacnet`（twin: `sbco:deviceIdBacnet` 他）に保持し、`local_id` には OPC-UA の
> nodeId（`ns=2;s=<point_id>` = `ns=2;s=SOS-PT-00X`）を格納**しています。この値は
> `../opcua-sim-gateway` が生成する OPC UA node_id 採番（`ns=2;s=<point_id>`）と一致します。
> OPC-UA nodeId は twin スキーマ外ではなく `sbco:localId` として twin に含まれます。

## Building OS 側の使い方

**`docker compose -f docker-compose.oss.yaml up -d` は既定で `twin.ttl` を自動投入します**
（`building-os.api` の `OXIGRAPH_SEED_TTL_PATH=/fixtures/e2e/twin.ttl` — #124）。追加の操作は
不要で、起動後は `/resources` や下記の投入確認コマンドでそのまま確認できます。

### A) Web UI からインポート(別の Turtle に差し替えたい場合)

1. スタックを起動: `docker compose -f docker-compose.oss.yaml up -d`
2. `http://localhost:3000/admin/twin` を開く(`--profile webclient` で Web も compose 起動可)
3. 任意の Turtle を **replace** モードでアップロード → プレビューで件数を確認して適用

> 注意: 起動時シードは**起動のたびに**デフォルトグラフを全置換するため、ここでの手動編集は
> 次回起動時に `twin.ttl` へ巻き戻ります。恒久的に差し替えたい場合は B) を使ってください。

### B) 起動時シードで別の Turtle を使う

```bash
# 既定は twin.ttl。別ファイルに差し替える場合(./fixtures 配下のパスのみ有効。
# api サービスが ./fixtures を /fixtures として bind mount 済み):
OXIGRAPH_SEED_TTL_PATH=/fixtures/e2e/other.ttl \
  docker compose -f docker-compose.oss.yaml up -d

# 起動時シードを無効化し、手動投入(A)のみにする場合:
OXIGRAPH_SEED_TTL_PATH= docker compose -f docker-compose.oss.yaml up -d
```

投入確認:

```bash
curl 'http://localhost:5000/gateways/GW-SOS-001/pointlist'   # pointlist.json と等価
curl 'http://localhost:5000/telemetries/query?pointId=SOS-PT-001&latest=true'  # 空でOK（未投入時）
```

## nexus-gateway 側の使い方

gateway をこの point_id 集合で起動し、Building OS に上り/下りを通します（詳細は
[../../docs/onboarding-e2e-gateway.md](../../docs/onboarding-e2e-gateway.md) の Step D）。

```bash
cd ../nexus-gateway
GATEWAY_ID=GW-SOS-001 \
BOS_ADDR=localhost:5051 \
BOS_INSECURE=true \
PROVISIONING_FILE=../gutp-building-os-ri/fixtures/e2e/pointlist.csv \
go run ./cmd/gateway
```

- `GATEWAY_ID` は twin の `sbco:gatewayId`（`GW-SOS-001`）と一致させます。
- twin から同期させる場合は `PROVISIONING_URL=http://localhost:5000/gateways/GW-SOS-001/pointlist`
  を指定（ETag / `If-None-Match`→304 / `?since=` 差分）。
- Building OS 側で gRPC ingress を有効化: `GRPC_INGRESS_PORT=5051`（Step D-1）。

> nexus-gateway の provisioning ファイルのカラム名がリポジトリ側で異なる場合は、
> `pointlist.csv` のヘッダを nexus-gateway の期待スキーマへリネームしてください
> （データ本体は同一です）。

## ../opcua-sim-gateway 側の使い方

`../opcua-sim-gateway` は **SBCO 標準ポイントリスト 1 枚から仮想 OPC UA サーバを生成する
シミュレータ**です（北向き = OPC UA。Building OS には直接繋がず、上位の OPC UA クライアント＝
接続ゲートウェイ/OWS が取り込んで Building OS へ中継します）。本 fixture の `pointlist.csv` は
その拡張 SBCO 形式にそのまま適合し、`generate-yaml` で読み込めます。

```bash
cd ../opcua-sim-gateway
uv run opcua-sim generate-yaml ../gutp-building-os-ri/fixtures/e2e/pointlist.csv -o config/simulator.yaml
uv run opcua-sim run      # opc.tcp://0.0.0.0:4840 に 8 ノードを公開
```

生成される OPC UA ノードは `ns=2;s=<point_id>`（`node-id-numbering.md` §2）＝ **本 fixture の
`local_id` と一致**します（検証済み）:

| point_id | 生成 node_id | DataType | states |
|---|---|---|---|
| SOS-PT-001 | `ns=2;s=SOS-PT-001` | Double | – |
| SOS-PT-004 | `ns=2;s=SOS-PT-004` | Boolean | Off/On |
| SOS-PT-007 | `ns=2;s=SOS-PT-007` | Int32 | Off/Low/Medium/High |
| …他5点 | `ns=2;s=SOS-PT-00X` | Double/Boolean | – |

opcua-sim 互換のための本 fixture 側の対応:

- **`local_id` = `ns=2;s=<point_id>`**（= sim の node_id 採番）にそろえてある。twin の `sbco:localId`
  も同値なので、OPC UA クライアントが読む nodeId と Building OS の point 解決が一致します。
- **`states` 列**（`|` 区切り）を追加（opcua-sim は `states` を読む）。SBCO 標準の `labels`（`&&`）も
  併記してあります（同じ内容・別セパレータ）。
- **multistate 点（Fan Speed）は `point_type=multistate`** にしてあります。opcua-sim は
  `point_specification`（Command/Status→binary、Measurement/Metering/Setpoint→analog）からしか
  binary/analog を導出できず、多状態は導出できないため、多状態点だけ標準値 `multistate` を明示。

> **サンプル fixture との関係:** `../opcua-sim-gateway/tests/fixtures/sample_pointlist.csv` は
> opcua-sim 同梱の**別データセット**（`GW001` / `PT001..PT008` / AHU 例）で、本 fixture とは
> **内容は同期していません**。ただし CSV スキーマ（拡張 SBCO）には互換で、本 fixture の
> `pointlist.csv` を `generate-yaml` に渡せば **本データセットで一気通貫**の検証ができます。
> 一気通貫では sample_pointlist.csv ではなく本 `pointlist.csv` を使ってください。

## 一気通貫（Building OS ⇔ 接続GW ⇔ opcua-sim）の流れ

```
opcua-sim (仮想 OPC UA サーバ, pointlist.csv 由来, ns=2;s=SOS-PT-00X)
   │  OPC UA (opc.tcp)  … 北向き
   ▼
接続ゲートウェイ / OWS (OPC UA クライアント, GATEWAY_ID=GW-SOS-001)
   │  gRPC ingress (gateway_id, point_id, value)  … point-id 正準
   ▼
Building OS (twin.ttl 投入済み, GET /gateways/GW-SOS-001/pointlist が正本)
```

- **Q. twin.ttl を取り込めば gateway にも同期される？** — **接続ゲートウェイ（point-list sync 対応、
  例: nexus-gateway）は Yes**。twin が正本で、gateway は `GET /gateways/GW-SOS-001/pointlist`
  （ETag ポーリング / NATS プッシュ）で追従します。**opcua-sim は No**（Building OS クライアントを
  持たない純粋な OPC UA サーバ）。opcua-sim には同じ `pointlist.csv` を `generate-yaml` で個別に
  与えてください（正本 twin と同一データセットなので内容は一致します）。

## 上り / 下りの確認（E2E）


```bash
# 上り（テレメトリ）: gateway から流し込んだ最新値
curl 'http://localhost:5000/telemetries/query?pointId=SOS-PT-001&latest=true'

# 下り（制御）: writable 点は 202 + controlId、read-only 点は 403
curl -X POST 'http://localhost:5000/points/SOS-PT-006/control' \
  -H 'Content-Type: application/json' -d '{"value": 22}'   # 202（16–30 の範囲内）
curl -X POST 'http://localhost:5000/points/SOS-PT-001/control' \
  -H 'Content-Type: application/json' -d '{"value": 1}'    # 403（read-only）
```

実 egress（GatewayBridge → gateway → シミュレータ WriteProperty）まで通すには、対象 gateway の
binding を `bacnet-sim` にします（`GatewayConnectionTypes__Map__GW-SOS-001=bacnet-sim`）。OSS 既定の
`ENABLE_SIM_CONTROL=true` はシミュレート制御で常に成功扱いになる点に注意。
