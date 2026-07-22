# SBCO オントロジー 標準語彙対応表

リソース表現（建物 → 階 → 空間 → 機器 → ポイント）の RDF/SPARQL 実装は
**SBCO（スマートビルコントロールオープン）オントロジーを主語彙として採用**している。
本書は SBCO 語彙と主要ビルディングオントロジー標準（Brick / REC / IFC / DTDL）との対応関係、
および SBCO に等価語彙がない箇所で残存する Building OS 固有拡張（`bos:`）を整理する。

> **HITL 必須**: SBCO クラス/プロパティ ↔ 外部標準（Brick / REC / IFC / DTDL）の**意味的等価性**は
> SBCO / GUTP ワーキングのドメインエキスパートによるレビュー対象である。各表に併記する
> `bos:`（旧/拡張）↔ 標準の列は、旧実装時に標準仕様書と照合済みの情報をトレーサビリティとして保持する。

## 凡例

| ラベル | 意味 |
|--------|------|
| **完全一致** | 概念・スコープが実質的に等価 |
| **部分一致** | 方向性は同じだが粒度・スコープが異なる |
| **拡張** | 標準に概念はあるが SBCO / `bos:` がセマンティクスを追加 |
| **独自** | 対応する標準語彙がない Building OS 固有概念 |

## 名前空間

```
sbco: = https://www.sbco.or.jp/ont/                       ← 主オントロジー
bos:  = http://buildingos.gutp.jp/ontology#               ← Building OS 固有拡張（SBCO に等価なし）
brick: = https://brickschema.org/schema/Brick#
rec:   = https://w3id.org/rec#
ifc:   = https://standards.buildingsmart.org/IFC/DEV/IFC4_1/OWL#
dtdl:  = dtmi: (Azure Digital Twins Definition Language)
```

SBCO ではノード URI そのものが Digital Twins ID（DtId）を表す（変換なし）。
`bos:` は `ControlSchema` / `dataType` / `enumLabels` という SBCO に等価語彙がない制御スキーマ概念にのみ残存する。

---

## 1. クラス対応表

主語は SBCO クラス。`bos:`（旧/拡張）列は旧実装でのクラス名（移行トレーサビリティ）。

### 空間階層

| SBCO クラス | bos:（旧/拡張） | Brick | REC | IFC | DTDL (ADT) | ラベル |
|------------|----------------|-------|-----|-----|-----------|--------|
| `sbco:Site` | —（旧実装になし） | `brick:Site` | `rec:Site` | `IfcSite` | `dtmi:org:w3id:rec:Site;1` | **部分一致**[^site] |
| `sbco:Building` | `bos:Building` | `brick:Building` | `rec:Building` | `IfcBuilding` | `dtmi:org:w3id:rec:Building;1` | **完全一致** |
| `sbco:Level` | `bos:Level` | `brick:Floor` | `rec:Level` | `IfcBuildingStorey` | `dtmi:org:w3id:rec:Level;1` | **完全一致** |
| `sbco:Room` | `bos:Room` | `brick:Room` | `rec:Room` | `IfcSpace` | `dtmi:org:w3id:rec:Room;1` | **部分一致**[^1] |

`sbco:Building` / `sbco:Level` / `sbco:Room` は SBCO の Architecture → Space サブクラス階層に属し、
**`Ext` サフィックスを持たない**（`sbco:SpaceExt` は存在しないため空間ノードには `sbco:Room` を用いる）。

[^site]: `sbco:Site` は SBCO の最上位コンテナ（敷地）。旧 `bos:` 実装には対応クラスがなかった。SBCO ↔ 標準の意味的等価性は HITL 確認対象。
[^1]: IFC の `IfcSpace` は廊下・吹き抜けなど部屋以外の空間も包含する。Brick の `brick:Room` と `brick:Zone` も区別があるが、SBCO の `sbco:Room`（および旧 `bos:Room`）は REC の `rec:Room` に準拠。

### 機器・ポイント

`sbco:EquipmentExt` / `sbco:PointExt` は **`Ext` サフィックスを持つ SBCO 拡張クラス**。

| SBCO クラス | bos:（旧/拡張） | Brick | REC | IFC | DTDL (ADT) | ラベル |
|------------|----------------|-------|-----|-----|-----------|--------|
| `sbco:EquipmentExt` | `bos:Equipment` | `brick:Equipment` | —（直接対応なし） | `IfcDistributionElement` | `dtmi:org:brickschema:schema:Brick:Equipment;1` | **部分一致**[^2] |
| `sbco:PointExt` | `bos:Point` | `brick:Point` | —（直接対応なし） | —（直接対応なし） | `dtmi:jp:gutp:Point;1` | **部分一致**[^3] |
| `bos:ControlSchema` | （拡張のまま） | —（直接対応なし） | —（直接対応なし） | —（直接対応なし） | `dtmi:jp:gutp:bim:ControlSchema;1` | **独自**[^4] |

[^2]: Brick は `brick:HVAC_Equipment` / `brick:Lighting_Equipment` などサブクラスを多数定義するが、`sbco:EquipmentExt` は connector worker の実装単純化のため単一クラスに統一している（機器種別は `sbco:deviceType`、点種別は `sbco:hasPoint` 経由の `sbco:pointType` で表現）。
[^3]: Brick は `brick:Sensor` / `brick:Setpoint` / `brick:Status` / `brick:Alarm` を区別するが、`sbco:PointExt` は IoT テレメトリの抽象化として単一クラスを採用。センサー／アクチュエーター区別は `sbco:writable` / `sbco:pointSpecification` と `point_id` の命名規則で表現。
[^4]: デバイス制御の入力スキーマ（`dataType`・`enumLabels`）を保持する Building OS 固有概念。SBCO にも外部標準にも等価語彙がないため `bos:` 名前空間に残存。DTDL の Telemetry + Command を組み合わせた構造に近いが完全には対応しない。

---

## 2. プロパティ（関係）対応表

| SBCO プロパティ | 方向 | Brick | REC | IFC | DTDL (ADT) | ラベル |
|----------------|------|-------|-----|-----|-----------|--------|
| `sbco:hasPart` | Site→Building, Building→Level, Level→Room | `brick:isLocationOf`（逆） | `rec:isPartOf`（逆） | `IfcRelAggregates` | `hasPart` リレーションシップ | **部分一致**[^5] |
| `sbco:locatedIn` | EquipmentExt→Room | `brick:isLocationOf` | `rec:locatedIn` | `IfcRelContainedInSpatialStructure` | `locatedIn` リレーションシップ | **完全一致**[^loc] |
| `sbco:hasPoint` | EquipmentExt→PointExt | `brick:hasPoint` | —（直接対応なし） | —（直接対応なし） | `hasPoint` リレーションシップ | **完全一致**（Brick 準拠） |
| `sbco:floor` | EquipmentExt→（Level 名の文字列） | —（命名規約） | — | — | — | **独自**[^floor] |

[^5]: Brick は空間包含に `brick:isLocationOf` / `brick:hasPart` 両方を文脈で使い分ける。`sbco:hasPart` は REC の構成関係（Site has Building has Level has Room）に相当し、REC の `rec:isPartOf` の逆方向。
[^loc]: SBCO サンプルデータ（TTL）によっては `sbco:Room` ノードや `sbco:locatedIn` 関係を含まない場合がある。その際、空間でフィルタするクエリは空を返し、詳細応答の space フィールドは空になる。
[^floor]: `sbco:floor` は EquipmentExt 上の**文字列リテラル**で、Level の `sbco:name` と突合して機器を階に紐づける（SBCO サンプルでは building → equipment の唯一の経路）。RDF リレーションシップではなく命名規約による結合のため独自。

---

## 3. データプロパティ対応表

`sbco:PointExt` / `sbco:EquipmentExt` の主なデータプロパティと各標準の対応。

| SBCO プロパティ | 説明 | Brick 相当 | REC 相当 | ラベル |
|----------------|------|-----------|---------|--------|
| `sbco:id` | ビジネス識別子（API / DB で使用） | —（Brick はエンティティを IRI で識別；専用 predicate なし）[^6] | `rec:id` | **部分一致** |
| `sbco:name` | 人間可読名称 | `rdfs:label` | `rdfs:label` | **完全一致** |
| `sbco:pointType` | 点種別（Temperature / CO2 等） | `brick:Tag` | — | **部分一致** |
| `sbco:pointSpecification` | 点の仕様区分（Measurement 等） | `brick:Tag` | — | **部分一致** |
| `sbco:writable` | 制御可否（true/false 文字列） | — | — | **拡張** |
| `sbco:gatewayId` | ゲートウェイ識別子 | — | — | **独自** |
| `sbco:deviceType` | 機器種別（EquipmentExt 上） | `brick:Tag` | — | **部分一致** |
| `bos:dataType` | 制御値のデータ型（bos: 残存拡張） | `brick:Tag` | — | **独自**（ControlSchema 用） |
| `bos:enumLabels` | 制御列挙値ラベル（bos: 残存拡張） | — | — | **独自**（ControlSchema 用） |

> ノード URI = DtId を採用しているため、旧 `bos:dtId`（Azure Digital Twins 由来の元 DT 識別子を保持する独自プロパティ）は不要となり廃止された。

[^6]: Brick Schema ではエンティティの同一性は RDF IRI（URI）で表現される。`sbco:id` のような「ビジネス識別子」に相当する専用 predicate は存在しないため直接対応なしとした。`rec:id` は REC が導入するリテラル識別子プロパティであり `sbco:id` の意味に最も近い。

---

## 4. テレメトリフィールド対応表

`valid-message.json` の正規化済みフィールドと各標準の対応。

| `bos:` フィールド（NATS payload） | Brick | IFC | ラベル |
|--------------------------------|-------|-----|--------|
| `point_id` | `brick:Point` URI | —  | **部分一致**（Brick では URI で表現） |
| `device_id` | `brick:Equipment` 識別子 | `IfcGUID` | **部分一致** |
| `building` | `brick:Building` 識別子 | `IfcBuilding.GlobalId` | **部分一致** |
| `value` | `brick:value` | `IfcPropertySingleValue` | **完全一致** |
| `datetime` | ISO 8601 タイムスタンプ | `IfcDateTime`（IFC4）[^7] | **部分一致** |
| `name` | `rdfs:label` | `IfcLabel` | **完全一致** |
| `data` | —（プロトコル固有属性） | `IfcPropertySet` | **部分一致**（JSONB で自由構造） |

> テレメトリ payload のフィールド名は NATS 正規化メッセージ仕様（`telemetry-specification.md`）に準拠し、
> オントロジーの名前空間プレフィックスは付さない。意味的にはそれぞれ上記 SBCO / 標準クラスへ対応する。

[^7]: IFC2x3 の `IfcTimeStamp` は REAL 型（POSIX 秒数）であり ISO 8601 文字列とは表現形式が異なる。IFC4 で導入された `IfcDateTime` が ISO 8601 文字列に相当するが、IFC バージョンによって対応概念が異なるため「部分一致」とした。

---

## 5. SBCO オントロジー対応（実装済み）

リソースグラフの主オントロジーは SBCO であり、以下のクラス/プロパティが OxiGraph 実装で実際に使われている
（`DotNet/BuildingOS.Shared/Infrastructure/OxiGraph/OxiGraphOntology.cs` が正本）。

### クラス

| 内部モデル | SBCO クラス | 備考 |
|-----------|------------|------|
| （最上位コンテナ） | `sbco:Site` | TTL 最上位。内部 C# モデルには未マップ |
| `Building` | `sbco:Building` | Architecture→Space サブクラス（Ext なし） |
| `Floor` | `sbco:Level` | 同上 |
| `Space` | `sbco:Room` | `sbco:SpaceExt` は存在しないため `Room` を使用 |
| `Device` | `sbco:EquipmentExt` | SBCO 拡張（Ext 付き） |
| `Point` | `sbco:PointExt` | SBCO 拡張（Ext 付き） |
| `ControlSchema` | `bos:ControlSchema` | SBCO に等価語彙なし → `bos:` 残存 |

### プロパティ

- SBCO: `sbco:id` / `sbco:name` / `sbco:hasPart` / `sbco:locatedIn` / `sbco:hasPoint` / `sbco:writable` / `sbco:gatewayId` / `sbco:pointType` / `sbco:pointSpecification` / `sbco:floor` / `sbco:deviceType`
- bos: 残存拡張: `bos:dataType` / `bos:enumLabels`（ControlSchema 用）
- ノード URI = DtId（変換なし）

> 補足: `OxiGraphOntology.cs` が定数の正本だが、`sbco:deviceType` のみ例外で `OxiGraphOntology.cs` には
> 定数が無く、`DeviceTemplateValidator.cs` の SPARQL 内に直接記述されている（`sbco:deviceType` は
> `sbco:EquipmentExt` に付与される）。上記一覧は「実装で実際に使われる SBCO プロパティ」の意味であり、
> 全てが `OxiGraphOntology.cs` の定数というわけではない。

> SBCO ↔ 外部標準（Brick / REC / IFC / DTDL）の意味的等価性については §1〜§4 を参照。
> 標準との対応付けは HITL 確認対象（本書冒頭の注記参照）。

---

## 6. `bos:` 残存拡張の根拠

SBCO に等価語彙がないため `bos:` 名前空間に残している概念とその理由。

| 残存拡張 | 採用しなかった標準 | 理由 |
|---------|----------------|------|
| `bos:ControlSchema` | DTDL Command / Brick Tag | デバイス制御 API のペイロード検証と UI 生成に必要な `enumLabels` を保持する概念が SBCO・既存標準にないため |
| `bos:dataType` | DTDL schema / Brick Tag | 制御値のデータ型（`boolean` / `number` / `enum`）を ControlSchema に保持するための拡張 |
| `bos:enumLabels` | — | 制御列挙値のラベル（例: `{"1":"冷房","2":"暖房"}`）を保持する Building OS 固有概念 |

> 機器種別は SBCO 単一クラス（`sbco:EquipmentExt`）+ `sbco:deviceType` で表現し、Brick のサブクラス体系
> （`HVAC_Equipment` 等）は採用していない。点種別も同様に `sbco:PointExt` + `sbco:pointType` /
> `sbco:writable` で表現し、Brick の `Sensor` / `Setpoint` / `Alarm` 区別は採らない（ConnectorWorker の実装統一のため）。

---

## 7. 既存ドキュメントとの連結

| ドキュメント | 内容 | 本書との関係 |
|------------|------|------------|
| [`oss-sparql-mapping.md`](oss-sparql-mapping.md) | ADT クエリ → SBCO SPARQL 変換対照表 | ADT DTMI → SBCO クラス/プロパティのマッピング実装詳細 |
| [`telemetry-specification.md`](telemetry-specification.md) | NATS 正規化済みメッセージ仕様 | §4 テレメトリフィールドの正本 |
| `DotNet/BuildingOS.Shared/Infrastructure/OxiGraph/OxiGraphOntology.cs` | SBCO 名前空間・クラス・プロパティ定数 | 主オントロジーの実装正本 |
| `DotNet/BuildingOS.Shared/Defines/Schemas/` | JSON Schema（テレメトリエンティティの source of truth） | テレメトリ payload の実装形 |

---

*更新: 2026-06-10（SBCO 主・`bos:` 残存拡張へ整合）/ HITL レビュー: SBCO ↔ 外部標準の意味的等価性は確認対象*
