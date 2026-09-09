# IPA「スマートビル システムアーキテクチャガイドライン」対応表

原典: スマートビル システムアーキテクチャ ガイドライン
発行者: 独立行政法人情報処理推進機構（IPA） デジタルアーキテクチャ・デザインセンター スマートビルプロジェクト
原典PDF: <https://www.ipa.go.jp/digital/architecture/Individual-link/ps6vr70000016bq2-att/smartbuilding_system-architecture_guideline.pdf>
参照日: 2026-09-09
照合対象: gutp-building-os-ri `main`（コミット `aae4830` 時点）

> 同じ内容を GitHub Pages でも公開しています:
> <https://gutp-bim.github.io/gutp-building-os-ri/ipa-guideline.html>
> （ソースは [`docs/ipa-guideline.html`](../ipa-guideline.html)。本ファイルが内容の正本で、
> 表を変更したときは両方を更新してください）

ガイドライン原典（全66ページ）のうち、必要／推奨が明記された表をすべて特定し（表3「要請の程度を
表す語句」で **必要** =「設計原則を満たすために必要」、**推奨** =「目的や用途に応じて選択的に採用」
と定義）、その全27項目を gutp-building-os-ri の実コード（コントローラ・クラス・SPARQL 等）と
1件ずつ突き合わせた結果です。該当する表は表4〜表9 の6つで、これが原典に存在する必要／推奨項目の
**全数**です（文書内 grep で確認済み、他に該当表なし）。

## Building OS の位置づけ

ガイドラインはスマートビルを **フィールド層 / データ共有・管理層 / アプリケーション層** に分けて
記述します。gutp-building-os-ri はこのうち **「データ共有・管理層の中間基盤」** に相当し、

- フィールド層の連携ゲートウェイ（連携GW）そのものは実装しません。GW 実装は nexus-gateway 等の
  隣接リポジトリの責務で、本リポジトリが持つのはその**受け口**（gRPC GatewayIngress / GatewayEgress、
  MQTT / Hono コネクタ）です。
- アプリケーション層のユースケース（BEMS アプリ、分析アプリ等）も実装しません。本リポジトリは
  REST / gRPC API と権限モデルを提供し、その上に載るアプリは利用者側の責務です。

後述の集計で「推奨」要件の充足率が「必要」要件より大きく落ちるのは、実装の抜けというより、この
スコープの絞り込みが表に出たものです。全体構成は
[システムアーキテクチャ](../architecture/system-architecture.md)、層構造は
[Hot / Warm / Cold アーキテクチャ](../architecture/oss-tier-architecture.md)、オントロジーの
標準対応は [標準規格マッピング](../architecture/standard-mapping.md) を参照してください。

## 凡例

- ✅ = 対応（実装済み）
- △ = 部分対応（機能は達成できるが、ガイドラインが想定する経路・粒度と差がある）
- ❌ = 未対応（相当する実装が無い、またはスコープ外）

## 表4: ビルOS連携ゲートウェイ（連携GW）

フィールド層の機能。**gutp-building-os-ri 自体は連携GWを実装しない**（連携GW実装は nexus-gateway 等の
隣接リポジトリの責務）。ここでは Building OS 側の受け口を示す。

| # | 機能 | 必要/推奨 | 状態 | 実装 |
|---|---|---|---|---|
| 1 | 設備稼働データ送信機能 | 必要 | ✅ | GatewayIngress gRPC（client-stream telemetry）、MQTT/Hono コネクタが受信側として対応 |
| 2 | コマンド送受信機能 | 推奨 | ✅ | GatewayBridge の GatewayEgress（bidi stream）が制御コマンドを GW へ中継 |
| 3 | 遠隔アップデート機能 | 推奨 | ❌ | GW 自体のソフトウェア更新は Building OS の管轄外 |

## 表5: データ送受信モジュール

ConnectorWorker の ingress 側。

| # | 機能 | 必要/推奨 | 状態 | 実装 |
|---|---|---|---|---|
| 1 | デバイス通信（受信）機能 | 必要 | ✅ | GatewayIngress gRPC、`MqttIngressWorker`、`AmqpIngressWorker` |
| 2 | データルーティング機能 | 必要 | ✅ | NATS `building-os.raw.*` → 正規化 → `building-os.validated.telemetry` → 各 Writer |
| 3 | データフィルタリング機能（異常値判定） | 推奨 | ❌ | ingestion 時の値レンジ検証は無い。`bos:alarmHigh/warnHigh/alarmLow` は UI 表示用の閾値のみで、取り込み経路のフィルタには使われていない（`ProtocolConnectorBase.ProcessAsync` は値をそのまま通す） |
| 4 | デバイス通信（送信）機能 | 推奨 | ✅ | GatewayEgress、`NatsPointControlWorker` |
| 5 | 連携GW遠隔アップデート機能 | 推奨 | ❌ | 表4-3 と同じ理由 |
| 6 | デバイス認証機能 | 必要 | ✅ | `GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY`（mTLS trusted header 検証）、MQTT/Hono 資格情報 |

## 表6: データ管理モジュール

| # | 機能 | 必要/推奨 | 状態 | 実装 |
|---|---|---|---|---|
| 1 | リアルタイムデータ管理機能 | 必要 | ✅ | NATS KV（`telemetry-latest`、`history=1`＝監視点ごとの最新値、`NatsKvLatestStore`）に集約・保存し、`GET /telemetries/query?latest=true` で監視点IDから特定可能。**注釈**: (a) 原文の「時刻…を指定した問い合わせ」はこの hot 層単体では不可（`IHotTelemetryStore.GetAsync` に時刻引数なし、`OssTelemetryQueryRouter` は時刻範囲指定時は常に warm/cold へルーティング）——時刻指定問い合わせは隣接する表6-2（アーカイブ）が担う役割分担。(b) 「受信日時」は独立したメタデータではなく、デバイス時刻と受信時刻フォールバックが単一の`Datetime`フィールドに混在（`GatewayIngressService.NormalizeTimestamp`、既知の課題として#418で言及済み） |
| 2 | アーカイブデータ管理機能 | 推奨 | ✅ | Parquet レイク（MinIO、warm/cold）、CompactionWorker/LakeRetention |
| 3 | 連携システム情報管理機能 | 必要 | △ | フィールド層 GW の接続情報レジストリ（`IGatewayConnectionRegistry`）はあるが、**アプリケーション/プラットフォーム側**の接続先・認証情報を管理する専用レジストリは無い。近い機能として OIDC クライアント管理（`OidcClientsController`）があるが、これは Building OS へ「入ってくる」認証の管理であり、ガイドラインが指す「連携システムの一覧・接続先」とは向きが異なる |
| 4 | 連携システムデータ管理機能 | 推奨 | ❌ | 外部アプリ/プラットフォームが保有するデータを Building OS 側に保存・管理する仕組みは無い |

## 表7: 建物デジタルツインモジュール

| # | 機能 | 必要/推奨 | 状態 | 実装 |
|---|---|---|---|---|
| 1 | 建物アセットデータ参照機能 | 必要 | ✅ | OxiGraph SPARQL + REST API（`/resources`、`GetPointDetailByPointId` 等） |
| 2 | 建物データモデル管理機能 | 必要 | △ | 管理者による Twin import（replace/append）は実装済み。UI ルートは web-client の `/admin/twin`、実体は API の `POST /api/admin/twin/import/preview` / `POST /api/admin/twin/import/apply`（`TwinAdminController`、`[AuthorizeFilter]` ＋ 各アクションの `IsAdmin()` で管理者限定）。ただしガイドラインが想定する「アプリケーション層のシステムのリクエストに応じた自動更新」経路は無く、常に管理者操作が起点 |

## 表8: データ連携モジュール

| # | 機能 | 必要/推奨 | 状態 | 実装 |
|---|---|---|---|---|
| 1 | データ提供機能 | 必要 | ✅ | REST/gRPC（`GET /telemetries/query`、resource API） |
| 2 | 遠隔制御コマンド送信機能 | 推奨 | ✅ | `POST /points/{id}/control` |
| 3 | ブローカー機能 | 推奨 | △ | `POST /api/assistant/chat` が外部 LLM へプロキシする例のみ（#151、既定オフの実験的機能、chat 専用）。リクエスト内容に応じて連携システムへ動的に振り分ける汎用ブローカーは無い |
| 4 | アプリケーション認証機能 | 必要 | ✅ | Keycloak OIDC/JWT（`JwtBearerDefaults`） |
| 5 | 権限管理機能 | 必要 | ✅ | `AuthorizationContext`/`IAuthorizationService.CanAccessAsync`、`{resourceType}:{resourceId}:{actions}` 形式の権限文字列。ガイドラインが想定する「連携アプリごと」の権限というよりは「ユーザー・ロールごと」の権限として実装されている点に留意 |

## 表9: 概念モデルの要件（建物データモデル）

| # | 要件 | 必要/推奨 | 状態 | 実装 |
|---|---|---|---|---|
| 1 | 空間・物体・監視点の3概念＋関係性を表現できること | 必要 | ✅ | `sbco:Room`（空間）/ `EquipmentExt`（物体）/ `PointExt`（監視点）+ `locatedIn`/`hasPoint` |
| 2 | 空間同士の関係性を表現できること | 必要 | ✅ | ガイドライン本文が示す具体例（図14）は「建物→フロア→部屋」の**木構造**（空間トポロジー）であり、`sbco:hasPart`（Site→Building→Level→Room）が満たす。「間仕切り変更等に応じた動的な変更」は本文でも「望ましい」（推奨相当）止まりで、Twin import（replace/append）で対応可能。なお、部屋同士の**隣接**関係（ドアで繋がっている等の横方向トポロジー）はこの要件の対象外で、ガイドラインの最低要件を超える拡張として #440 で別途提案している |
| 3 | 空間と物体の関係性を動的に変更できること | 必要 | ✅ | Twin import（replace/append）により `locatedIn` の再構成が可能 |
| 4 | オブジェクトはプロパティ（データ項目）を持つこと | 必要 | ✅ | `sbco:id/name/pointType` 等、`bos:dataType` 等 |
| 5 | 空間・物体・監視点のグループ化（グラフ構造が推奨） | 推奨 | ❌ | `Zone`/`Area` に相当するクラスが無い。関係はすべて木構造（`hasPart`/`locatedIn`） |
| 6 | 物体同士の関係性を表現できること | 推奨 | ❌ | Equipment↔Equipment の関係述語（Brick の `feeds`/`isFedBy` 相当）が存在しない |
| 7 | オブジェクトが「位置情報」プロパティを持つこと | 推奨 | ❌ | 生きているモデルには存在しない。自動生成 DTDL エンティティ（`Dtdl.Site.*.cs`）に `RefLatitude`/`RefLongitude` があるが、リポジトリ全体でどこからも参照されない未使用コード |

## Summary

| 区分 | 項目数 | ✅対応 | △部分対応 | ❌未対応 |
|---|---|---|---|---|
| 必要（15項目） | 15 | 13 (87%) | 2 (13%) | 0 (0%) |
| 推奨（12項目） | 12 | 4 (33%) | 1 (8%) | 7 (58%) |
| 合計（27項目） | 27 | 17 | 3 | 7 |

**注目点**: 「必要」要件15項目は❌ゼロ（✅13・△2）で、ガイドラインが**必須**と明記する最低要件は全て
満たしている（△の2件も、管理者操作を起点とした運用で機能自体は達成できている）。一方「推奨」要件は
12項目中7項目（58%）が未対応で、必要要件と比べて充足率が大きく落ちる——これは実装の抜けというより、
gutp-building-os-ri が「データ共有・管理層の中間基盤」に意図的にスコープを絞っている（連携システムの
データガバナンス、汎用ブローカー機能、位置情報、機器間関係、ゾーングルーピング等はアプリケーション層や
将来拡張に委ねる設計判断）ことの表れと解釈できる。#440（部屋間の隣接トポロジー）はこの「推奨」領域を
さらに拡張する提案であり、ガイドラインの必須要件を埋めるものではない点に注意。

## 本文書作成時の追加検証（Issue 本文への補足）

以下は #441 本文の表を本ドキュメントへ転記する際に、実コードで裏取りして見つかった補強点です。
判定（✅/△/❌）はいずれも変わらないため表本体は Issue 本文どおりとし、根拠の精度に関する補足を
ここに残します。

1. **表5-3（データフィルタリング機能）の根拠には `GatewayIngressService` も併記されるべき。**
   表の「実装」列が挙げる `ProtocolConnectorBase` は、それ自身の doc comment のとおり
   `HvacConnectorWorker` / `ElectricConnectorWorker` にのみ適用されるもので
   （`DotNet/BuildingOS.ConnectorWorker/Connectors/ProtocolConnectorBase.cs`）、**取り込みの正本経路である
   gRPC GatewayIngress はこの基底クラスを通らない**。その GatewayIngress 側
   （`DotNet/BuildingOS.ConnectorWorker/Connectors/GatewayIngressService.cs`）の skip 条件は
   「gateway_id/point_id 欠落」「ゲートウェイ ID 同一性（#296）」「未知の point_id」「ツイン上の所有権」
   「建物階層への接続（#292）」の5つのみで、**値レンジ検証は存在しない**。つまり ❌ 判定は正本経路でも
   同様に成り立ち、根拠としてはこちらを挙げる方が正確。

2. **表9-7（位置情報プロパティ）は「プロパティが未参照」より強く言える。**
   `RefLatitude` / `RefLongitude` の出現箇所は JSON Schema
   （`DotNet/BuildingOS.Shared/Defines/Schemas/dtdl.json`）と、そこから生成された
   `Defines/Entities/Dtdl.Site.Properties.cs` / `Dtdl.Site.Validate.Object.cs` のみで、`latitude` /
   `longitude` は DotNet・web-client・fixtures のいずれにも他に現れない。さらに正確には、
   **生成された `Dtdl.Site.*` 型そのものが `Defines/Entities/` の外から一切参照されていない**
   （`Dtdl.` の参照は生成物以外に 0 件）。加えて当該2プロパティは dtdl.json 上 `required` 指定であり、
   スキーマ上は必須でありながら実行時にはどの経路からも使われていない、という状態にある。

## 参照

- 原典PDF: [スマートビル システムアーキテクチャ ガイドライン](https://www.ipa.go.jp/digital/architecture/Individual-link/ps6vr70000016bq2-att/smartbuilding_system-architecture_guideline.pdf)（独立行政法人情報処理推進機構 デジタルアーキテクチャ・デザインセンター スマートビルプロジェクト）— 版数・発行日は PDF 本体に明示が無いため、URL と参照日で特定する
- [IPA スマートビルガイドライン一覧ページ](https://www.ipa.go.jp/digital/architecture/guidelines/smartbuilding-guideline.html)
- [標準規格マッピング（SBCO / `bos:` ↔ Brick / REC / IFC / DTDL）](../architecture/standard-mapping.md)
- [システムアーキテクチャ](../architecture/system-architecture.md)
- [Gateway Bridge ingress/egress split](../architecture/gateway-bridge-ingress-egress-split.md)
- [制御の安全性設計](../architecture/oss-control-safety.md)
- [Azure / OSS 機能比較](oss-feature-comparison.md)（同型の前例）
- 関連 Issue: #440（部屋間トポロジー PRD — ガイドラインの必須要件〔表9-2、木構造で充足済み〕を超えた、隣接関係の拡張提案）、#418（受信日時とデバイス時刻の混在）、#151（Assistant chat プロキシ）
