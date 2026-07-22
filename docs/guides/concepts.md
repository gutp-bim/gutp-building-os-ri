# 基本概念 — Building OS を1ページで理解する

このページは **Building OS を初めて触る人**が、UI やドキュメントに出てくる用語で迷わないための
1ページ入門です。起動手順は [getting-started.md](getting-started.md)、アーキテクチャの詳細は
[system-architecture.md](../architecture/system-architecture.md) を参照してください。

> ℹ️ ここで説明する用語の多くは、Web UI 上でも 用語集ツールチップ（`GlossaryTooltip`）から引けます。
> 用語の正本は `web-client/src/lib/help/content.ts`（content-as-code, #149）です。

---

## 1. 設備の階層 — 5つの基本オブジェクト

Building OS は、ビル設備を次の階層でモデル化します。

```
Building（建物）
  └ Floor（フロア）
      └ Space（空間 / 部屋）
          └ Equipment（機器）
              └ Point（ポイント）
```

- **Building / Floor / Space** — 物理的な場所の入れ子。
- **Equipment（機器）** — 空調・電力計・環境センサーなどの設備。1台の機器は複数のポイントを持ちます。
- **Point（ポイント）** — **計測点・制御点の最小単位**。センサー値・設定値・運転状態・制御対象は
  すべて「ポイント」として表現されます。テレメトリ（時系列データ）も制御も、粒度はこのポイントです。

この5階層のノードをまとめて **リソース（Resource）** と呼びます。`/resources` 画面は、この階層を
建物からポイントまで辿るための入口です。

---

## 2. Point とは何か

1つのポイントは、次のいずれか（または複数）を表します。

| 種類 | 例 |
|---|---|
| センサー値 | 室温、CO₂ 濃度、消費電力 |
| 設定値 | 温度設定、風量設定 |
| 運転状態 | 運転/停止、モード |
| 制御対象 | 書き込み可能なポイント（後述の「制御」の対象） |

ポイントが書き込み可能（`writable`）かどうか、どんな値を取りうるか（制御スキーマ）は、
デジタルツイン上のメタデータとして定義されます。

---

## 3. ID の使い分け — `point_id` / `localId` / `gateway_id` / `device_id`

Building OS でいちばん混乱しやすいのが ID の種類です。役割ごとに分かれています。

| ID | 何を指すか | 誰にとっての正本か |
|---|---|---|
| **`point_id`** | ポイント1つ | **BuildingOS 内の正本**。テレメトリ・制御・認可はすべてこの ID を主語に扱う |
| **`localId`** | ゲートウェイ側でのポイントのプロトコル固有アドレス（例: BACnet の object/instance） | **現場側の別名**。ゲートウェイがこれを `point_id` に解決する |
| **`gateway_id`** | ゲートウェイ（中継器）1つ | ツイン全体で一意。どのポイント群を「所有」するかを決める |
| **`device_id`** | 設備（機器 / Equipment）1つ | どの設備か。複数の device が1つの gateway にぶら下がる |

覚え方:

- **`point_id` は BuildingOS 全体の正本、`localId` は現場側の別名**。両者は「ポイントリスト」
  （後述）で対応づけられます。
- **`gateway_id` は「どの中継器か」、`device_id` は「どの設備か」** — 粒度が違います。

---

## 4. データの2つの流れ — テレメトリ（入力）と制御（出力）

Building OS とゲートウェイの間には、**方向の異なる2つの経路**があります。

```
                テレメトリ（値の入力）
  ゲートウェイ ───────────────────────────▶ BuildingOS   … GatewayIngress
              （gateway_id + point_id + value + timestamp）

                制御（コマンドの出力）
  ゲートウェイ ◀─────────────────────────── BuildingOS   … GatewayEgress
              （point_id + present_value + priority）
```

- **GatewayIngress（テレメトリ取り込み）** — ゲートウェイが値を送る gRPC 経路。
  ConnectorWorker がホストします。値そのもの（gateway_id + point_id + value + timestamp）だけを運び、
  建物・機器名などの静的メタデータは BuildingOS 側がツインで補完します。
- **GatewayEgress（制御）** — BuildingOS がコマンドを送る gRPC 経路。GatewayBridge がホストします。

**入力と出力は別経路・別ポートに分離**されています（ingress = ConnectorWorker、egress =
GatewayBridge）。詳細は [gateway-integration.md](gateway-integration.md)。

---

## 5. 「最新値」と「履歴」 — Hot / Warm / Cold（階層ストレージ）

テレメトリは用途別に分けて保存されます（**階層ストレージ**）。利用者が意識するのは、ほぼ
「最新値か、履歴か」の違いだけです。

| 層 | 実体 | 利用者視点 |
|---|---|---|
| **Hot** | NATS KV | **最新値**（即時参照） |
| **Warm / Cold** | MinIO 上の Parquet レイク | **履歴**（時系列・集計） |

API 側は `GET /telemetries/query` が層を自動選択するため、利用者が層を指定する必要はありません。
鮮度モデルの詳細は [oss-sla-freshness.md](../operations/oss-sla-freshness.md)、階層の設計は
[oss-tier-architecture.md](../architecture/oss-tier-architecture.md) を参照してください。

関連: **鮮度切れ（stale）** — 最新値が「鮮度切れ閾値」（既定 300 秒）より古い場合に、UI 上で
古い値として表示されます。

---

## 6. デジタルツインとポイントリスト — 共有の正本

- **デジタルツイン（ツイン）** — 建物 → フロア → 空間 → 機器 → ポイントの階層と、その静的メタデータを
  保持するグラフモデル。**OxiGraph**（SPARQL / RDF グラフDB）で管理され、**共有ポイントリストの正本
  （source of truth）**です。
- **ポイントリスト** — あるゲートウェイが担当するポイントの一覧（native アドレス・単位・書込可否・
  制御スキーマ等）。ツインが正本で、ゲートウェイは `GET /gateways/{id}/pointlist` で追従します。
- **リビジョン（revision / ETag）** — ポイントリストの版数。内容ハッシュ（`"sha256:..."`）で表され、
  内容が変わったときだけ値が変わります。ゲートウェイは `If-None-Match` で問い合わせ、変化が無ければ
  `304`（＝再取得不要）が返ります。

新しいゲートウェイをオンボードする手順は
[gateway-onboarding-checklist.md](gateway-onboarding-checklist.md) に集約されています。

---

## 7. 「内部で使う語」— SBCO / OxiGraph

次の2語は**内部実装の語彙**です。利用者が日常的に意識する必要はありませんが、ドキュメントに出てくる
ので押さえておくと読みやすくなります。

- **SBCO** — Building OS が採用するビル設備の**オントロジー（語彙）**。建物・フロア・空間・機器・
  ポイントを `sbco:Building` / `sbco:Level` / `sbco:Room` / `sbco:EquipmentExt` / `sbco:PointExt`
  として定義します。Brick / REC / IFC / DTDL 標準との対応は [standard-mapping.md](../architecture/standard-mapping.md)。
- **OxiGraph** — デジタルツインを格納する SPARQL / RDF グラフデータベース。リソース検索やポイント解決の
  基盤です。

---

## 次に読む

- 🚀 [Getting Started（起動 → 読取 → 制御）](getting-started.md)
- 🏛️ [System Architecture（全体像）](../architecture/system-architecture.md)
- 🔗 [Gateway Integration（ゲートウェイ接続モデル）](gateway-integration.md)
- 🧭 [用語集の正本（content-as-code）](../../web-client/src/lib/help/content.ts)
