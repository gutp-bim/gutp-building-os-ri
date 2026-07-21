# 非数値テレメトリ値型（string / boolean / enum）の一級対応

- **Status**: Accepted（#152。**Phase A 実装済み** — 判別ユニオン型スパインを schema/proto/POCO/ingress/
  Parquet/API/frontend に end-to-end で導入、numeric は全経路後方互換。Open Decisions は D1=string+boolean
  一級化・enum 除外、D6=推奨どおり Phase A の粒度で確定。Phase B/C は未着手。）
- **関連**: #189（現行の数値単一型 + EnumLabels/`data` 回避策を明文化した設計判断）、#216/ADR-0002（Parquet lake
  warm store）、#224（point list / ControlSchema）、#158 Phase 2a（アラームは numeric 前提）

## Context

正規化済みテレメトリの `value` は **数値単一型**に固定されており、状態系ポイント（運転モード・列挙・ON/OFF・
文字列ステータス）を一級で表現できない。#189 でこれは意図的な設計判断として確定し、回避策
（boolean→0/1、enum→数値コード + `EnumLabels`、文字列→`data`/`attributes` 側路に型情報を落として添える）が
`docs/telemetry-specification.md` L124-136 に明文化されている。本 ADR は #152（この回避策を一級型に置き換える）
の**設計と段階計画**を確定する。

### 現状の型チェーン（file:line、これを変える）

`value` は pipeline 全層で numeric に固定されている。**pivot は
`ValidTelemetryData.Value`（`DotNet/BuildingOS.Shared/Domain/Entities/ValidTelemetryData.cs:14` = `double?`）**で、
上下流が連なる:

| 層 | 現状（numeric 固定）| 出典 |
|---|---|---|
| JSON Schema（正本）| `value: { type: number }`（required, `additionalProperties:false`）| `Defines/Schemas/valid-message.json:25-28,57` |
| 生成エンティティ | `ValidTelemetryEntity.Value: Corvus.Json.JsonNumber` | `Defines/Entities/ValidMessageJson.ValidTelemetryEntity.Properties.cs:331` |
| **ドメイン POCO（pivot）** | `double? Value` | `ValidTelemetryData.cs:14` |
| proto ingress | `double value = 3;` | `proto/gateway_ingress.proto:37` |
| proto egress（制御）| `double present_value = 6;` | `proto/gateway_egress.proto:62` |
| ingress→validated | `value: (JsonNumber)frame.Value`；IoT 経路は `JsonValueKind.Number` 以外を skip | `GatewayIngressService.cs:166`, `IoTIngressConnectorBase.cs:56,62` |
| Hot KV | JSON 直列化（型制約は POCO の `double?` のみ）| `NatsKvLatestStore.cs:50,62` |
| **Parquet lake** | `DataField<double?> Value`；読取は `is double` | `ParquetTelemetrySerializer.cs:18`, `ParquetLakeScan.cs:228`, envelope `ValidTelemetryEnvelope.cs:40,56-59` |
| Parquet rollup | `avg/min/max: double?` | `RollupSerializer.cs:14-16`, `TelemetryAggregator.cs:11-13,51-61` |
| Timescale warm（opt-in）| `value DOUBLE PRECISION`；連続集計 `AVG/MIN/MAX(value)` | `Migrations/Timescale/V001__telemetry_hypertable.sql:19,74-86`, `oss-stack/postgres/init.sql:24` |
| warm/agg 読取 | `reader.GetDouble(5)` | `NpgsqlWarmTelemetryStore.cs:72`, `NpgsqlAggregatedTelemetryStore.cs:62` |
| API DTO | `LatestSample(… double? Value)`；`ValidTelemetryData[]` 返却 | `TelemetryController.cs:293,272` |
| aspida 生成 | `value?: number \| null` | `aspida-client/generated/@types/index.ts:508,221` |
| frontend | `TelemetryPoint.v: number`；`typeof d.value === "number"` で非数値を **drop** | `telemetry/types.ts:10`, `telemetry/mapping.ts:33-37` |
| 非数値の唯一の生存路 | `data`/`attributes` 文字列側路（型情報は落ちる）| schema `valid-message.json:29-34`, proto `gateway_ingress.proto:42`, `ValidTelemetryData.Data:8` |

**要点**: numeric は全層でカラム/フィールドが `double` に固定。非数値を通すには pivot（POCO）を判別ユニオンに
変え、各層でその判別を運ぶ必要がある。**最難関は Parquet lake**（列指向で単一 `double` 列）と **Timescale**
（`DOUBLE PRECISION` 列 + numeric 前提の連続集計）。frontend の numeric チャートは `typeof==="number"` フィルタで
**非数値を静かに drop 済み**なので、既存表示は壊れない（＝後方互換の追い風）。

## Considered Options（ユニオンの表現）

### A. 判別子 + 型別カラム（判別ユニオン, 推奨）
`valueType ∈ {number,string,boolean}` を足し、numeric は**既存 `value`（double）列を維持**、string/boolean は
新カラム（`value_text` / `value_bool`）に格納。JSON 層（schema/KV/API/aspida）では `value` が number|string|boolean を
そのまま取り、`valueType` を併記。
- ✅ **numeric は完全後方互換**（既存 `value` 列/フィールド/クエリ/集計/チャートが無改変で動く）。新カラムは additive。
- ✅ 列指向の numeric 効率（avg/min/max）を維持。非数値は別カラムなので混在しない。
- ❌ カラム数が増える（Parquet 3 列 + 判別、Timescale ALTER ADD 3 列）。判別ロジックが各層に要る。

### B. 単一ポリモーフィック列（JSON/string + 型タグ）
`value` を string 化（or JSON）+ `valueType`。全型を1列に。
- ✅ カラム最小。❌ **numeric が既存 `double` 列でなくなり後方互換が壊れる**（既存 Parquet/Timescale データ・集計・
  チャートの全面移行が必要）。numeric 集計に毎回 cast。**却下**（移行コストと numeric 劣化が大きすぎる）。

### C. 現状維持（数値コード + `data` 側路 + EnumLabels, #189）
- ✅ 影響ゼロ。❌ 一級対応でない（型検証・検索・集計の対象外、変換ロジックをゲートウェイに強制）。#152 の
  否定対象そのもの。**却下**（本 ADR の目的は C の置換）。

→ **A（判別ユニオン + 型別カラム）を採用**。numeric を既存カラムに温存する後方互換性が決定的。

## Open Decisions（maintainer 承認が要る論点）

- **D1. 一級化する型の範囲**: `string` + `boolean` を一級化する（推奨）。`enum` は「安定コード + `EnumLabels`」の
  現行運用を活かしつつ、**enum を `string`（ラベル値）として送る**選択肢も一級 string で表現可能。boolean を
  一級にするか、`number(0/1)` + ControlSchema `dataType:boolean` の現行で足りるとするか（＝ string のみ一級化）も論点。
- **D2. 永続化の判別表現**: A の「`valueType` + `value`(double) + `value_text` + `value_bool`」でよいか。判別子の
  既定（旧データ・旧行で `valueType` 欠落 → `number` とみなす）を確認。
- **D3. 集計セマンティクス（非数値）**: 非数値は `avg/min/max` の対象外（numeric のみ）。非数値の履歴は
  **last-in-bucket / distinct-count / 状態継続時間**のどれを一級にするか（Phase B）。Phase A では **latest 値の表示のみ**
  （集計なし）を提案。
- **D4. proto 後方互換**: `TelemetryFrame.value` を `oneof value { double value_num = 3; string value_str = N; bool value_bool = M; }`
  にし、**field 番号 3 を double のまま維持**（既存 numeric ゲートウェイはワイヤ互換）。egress の `present_value`
  （制御書込）を非数値化するか（文字列 setpoint 等）は Phase を分けて判断（Phase A は ingress のみ、制御は numeric/bool 据置）。
- **D5. アラーム(#158 2a)との整合**: `classifyPointAlarm` は numeric 前提。非数値ポイントはアラーム評価対象外
  （`unknown`）に据え置く（将来 string/enum の「異常状態値」一致は別途）。
- **D6. 段階（下記 Phasing）**の承認。

## Decision（推奨サマリ, 承認待ち）

1. 表現 = **A 判別ユニオン**。POCO `ValidTelemetryData` を `{ ValueType, double? Value, string? ValueText, bool? ValueBool }`
   相当に拡張（`Value` は numeric 専用に温存）。schema `value` を `number|string|boolean` の union に、生成エンティティ再生成。
2. proto = `oneof value`（D4、field 3 = double 維持）。ingress のみ Phase A、egress は据置。
3. 永続化 = numeric は既存カラム維持、string/boolean は additive な新カラム + `valueType`。旧データは `number` 既定。
4. 集計 = numeric のみ avg/min/max。非数値は Phase A で latest 表示、Phase B で状態履歴。
5. C（EnumLabels/数値コード回避策）は当面**併存**（破壊しない）、一級 string/enum への移行は Phase C で任意。

## Phasing（段階導入 — 各段独立に価値・独立にレビュー/マージ）

- **Phase A — 型スパイン（representable end-to-end, latest 表示）**
  schema union + 生成エンティティ再生成 + POCO 判別ユニオン + proto `oneof`（ingress）+ `GatewayIngressService`/
  コネクタの型伝搬 + Hot KV（JSON, POCO 変更のみ）+ **Parquet writer/reader に判別 + 型別カラムを additive 追加**
  （非数値が validated に流れても writer が壊れない/落とさないため Phase A に含める）+ API DTO ユニオン化
  （numeric は `value:number` 温存 + `valueText`/`valueBool`/`valueType` 追加）+ frontend は非数値 latest をテキスト表示
  （チャートは numeric-only のまま非数値を skip）。**numeric は全経路無改変で通ることを回帰で保証**。
- **Phase B — 非数値の履歴 UX + 集計セマンティクス**
  状態タイムライン（last-in-bucket / distinct）クエリ、Timescale opt-in の additive 列 + 集計、frontend の状態/テキスト
  タイムライン表示。
- **Phase C — 数値コード + EnumLabels 回避策の一級化（任意）**
  enum を一級 string/enum として送る運用へ移行、`docs/telemetry-specification.md` #189 節を更新、回避策を deprecate。

## Consequences

- **numeric は完全後方互換**（既存カラム/フィールド/集計/チャート無改変）。移行は additive（Parquet 新列は旧ファイル
  読取で欠落→numeric 既定、Timescale は `ALTER TABLE ADD` でデータ書換なし）。
- 影響ファイルは広い（pivot POCO + 10 層）。Phase A だけでも schema/proto/生成/POCO/ingress/Parquet/API/frontend に
  跨る。**Corvus.Json 生成の union（`oneof`/`type:[...]`）出力**が想定通りかは実地確認が要る（本 ADR の最大の未検証点）。
- Parquet の型別カラム追加は lake の後方互換（旧 `part-*.parquet` を新 reader が読めること）を要検証。Docker/実 lake が
  要るため単体 + Testcontainers でカバー予定。
- #158 アラームは numeric のまま（非数値は評価対象外）。制御(egress)は Phase A では numeric/bool 据置。

## Open Questions（実装前に確定）

1. D1 型範囲（string のみ / string+boolean / enum 一級化の時期）。
2. D2 判別カラム設計と旧データ既定（number）。
3. D3 非数値の集計セマンティクス（Phase B）。
4. D4 proto oneof と egress 非数値の扱い。
5. D6 Phase A の粒度（Parquet 永続化まで含めるか、KV+latest だけの更に薄い先行スライスにするか）。

## 参照

- `docs/telemetry-specification.md`（#189 の設計判断、置換対象）。ADR-0002（Parquet lake）、ADR-0005（段階導入の先例）。
- コード（pivot と各層）: `ValidTelemetryData.cs`、`valid-message.json`、`ParquetTelemetrySerializer.cs` /
  `ParquetLakeScan.cs`、`proto/gateway_ingress.proto`、`GatewayIngressService.cs`、`TelemetryController.cs`、
  `NpgsqlWarmTelemetryStore.cs`、`web-client/src/lib/telemetry/{types,mapping}.ts`。
