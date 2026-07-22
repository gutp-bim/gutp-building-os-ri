# OxiGraph SPARQL Mapping: ADT Query → SBCO SPARQL

This document maps Azure Digital Twins (ADT) queries to their OxiGraph SPARQL equivalents.
The resource graph uses the **SBCO ontology** (`https://www.sbco.or.jp/ont/`) as its primary
vocabulary; `bos:` is retained only for the `ControlSchema` extension, which has no SBCO equivalent.

The implementation of record is
`DotNet/BuildingOS.Shared/Infrastructure/OxiGraph/OxiGraphOntology.cs` (namespace / class / property
constants), `DotNet/BuildingOS.Shared/Infrastructure/OxiGraph/OxiGraphDigitalTwinDatabase.cs` and
`DotNet/BuildingOS.Shared/Infrastructure/Authorization/OxiGraphHierarchyResolver.cs` (live SPARQL).

## Ontology Namespace

```
sbco: = https://www.sbco.or.jp/ont/                ← primary
bos:  = http://buildingos.gutp.jp/ontology#        ← ControlSchema extension only
rdf:  = http://www.w3.org/1999/02/22-rdf-syntax-ns#

Node URI = DtId  (no transformation; the node IRI itself IS the DtId)
```

> In SBCO the node URI *is* the DtId, so there is no `urn:dtid:{…}` prefixing. `NodeUri(dtId)` returns
> `dtId` unchanged. SBCO resource IRIs are percent-encoded business IDs under
> `https://www.sbco.or.jp/ont/resource/…`. Both binding styles are used: a **business ID** is matched
> via the `sbco:id` literal (e.g. `?dt sbco:id "{pointId}"`), while **graph traversal from a known node**
> binds by the node IRI directly (e.g. `<{buildingDtId}> sbco:hasPart ?dt`).

## RDF Class Mapping

| ADT Model ID | RDF Class | Description |
|---|---|---|
| `dtmi:org:w3id:rec:Building;1` | `sbco:Building` | Building |
| `dtmi:org:w3id:rec:Level;1` | `sbco:Level` | Floor |
| `dtmi:org:w3id:rec:Room;1` | `sbco:Room` | Space / Room (no `SpaceExt` in SBCO) |
| `dtmi:org:brickschema:schema:Brick:Equipment;1` | `sbco:EquipmentExt` | Device / Equipment (SBCO extension) |
| `dtmi:jp:gutp:Point;1` | `sbco:PointExt` | Sensor / Actuator point (SBCO extension) |
| `dtmi:jp:gutp:bim:ControlSchema;1` | `bos:ControlSchema` | Control input schema (no SBCO equivalent) |

`sbco:Building` / `sbco:Level` / `sbco:Room` are SBCO Architecture → Space subclasses (no `Ext` suffix);
`sbco:EquipmentExt` / `sbco:PointExt` carry the `Ext` suffix as SBCO extensions.

## RDF Relationship / Property Mapping

| ADT Relationship | RDF Property | Direction |
|---|---|---|
| `hasPart` | `sbco:hasPart` | Site→Building, Building→Level, Level→Room |
| `locatedIn` | `sbco:locatedIn` | EquipmentExt→Room |
| `hasPoint` | `sbco:hasPoint` | EquipmentExt→PointExt |
| (building scoping) | `sbco:floor` | EquipmentExt → Level **name** (string literal join) |

`sbco:floor` is a string literal on `sbco:EquipmentExt` matched against a Level's `sbco:name`; in the
current SBCO TTL it is the only path from a building down to its equipment.

## Query Mapping

### List All Buildings

**ADT:**
```sql
SELECT * FROM digitaltwins WHERE IS_OF_MODEL('dtmi:org:w3id:rec:Building;1')
```

**SPARQL:**
```sparql
PREFIX sbco: <https://www.sbco.or.jp/ont/>
SELECT ?dt ?id ?name WHERE {
  ?dt a sbco:Building ;
      sbco:id ?id ;
      sbco:name ?name .
}
```

---

### List Floors by Building

**ADT:**
```sql
SELECT target FROM digitaltwins source JOIN target RELATED source.hasPart
WHERE source.$dtId = '{buildingDtId}'
  AND IS_OF_MODEL(target, 'dtmi:org:w3id:rec:Level;1')
```

**SPARQL** (`{buildingDtId}` is the node IRI, inserted directly):
```sparql
PREFIX sbco: <https://www.sbco.or.jp/ont/>
SELECT ?dt ?id ?name WHERE {
  <{buildingDtId}> sbco:hasPart ?dt .
  ?dt a sbco:Level ;
      sbco:id ?id ;
      sbco:name ?name .
}
```

---

### List Spaces by Floor

**ADT:**
```sql
SELECT target FROM digitaltwins source JOIN target RELATED source.hasPart
WHERE source.$dtId = '{floorDtId}'
  AND IS_OF_MODEL(target, 'dtmi:org:w3id:rec:Room;1')
```

**SPARQL:**
```sparql
PREFIX sbco: <https://www.sbco.or.jp/ont/>
SELECT ?dt ?id ?name WHERE {
  <{floorDtId}> sbco:hasPart ?dt .
  ?dt a sbco:Room ;
      sbco:id ?id ;
      sbco:name ?name .
}
```

> The current SBCO TTL may not include `sbco:Room` nodes or `sbco:hasPart` to them; in that case this
> query returns empty and space fields in detail responses are empty.

---

### List Devices by Space (inverse `locatedIn`)

**ADT:**
```sql
SELECT source FROM digitaltwins source JOIN target RELATED source.locatedIn
WHERE target.$dtId = '{spaceDtId}'
```

**SPARQL** (gateway aggregated with `SAMPLE` across the device's points):
```sparql
PREFIX sbco: <https://www.sbco.or.jp/ont/>
SELECT ?devDt ?devId ?devName (SAMPLE(?gwRaw) AS ?devGw) WHERE {
  ?dev a sbco:EquipmentExt ;
       sbco:id ?devId ;
       sbco:name ?devName ;
       sbco:locatedIn <{spaceDtId}> .
  BIND(?dev AS ?devDt)
  OPTIONAL { ?dev sbco:hasPoint ?pt . ?pt sbco:gatewayId ?gwRaw . }
}
GROUP BY ?devDt ?devId ?devName
```

---

### List Points by Device

**ADT:**
```sql
SELECT target FROM digitaltwins source JOIN target RELATED source.hasPoint
WHERE source.$dtId = '{deviceDtId}'
```

**SPARQL:**
```sparql
PREFIX sbco: <https://www.sbco.or.jp/ont/>
SELECT ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw WHERE {
  <{deviceDtId}> sbco:hasPoint ?pt .
  ?pt a sbco:PointExt ;
      sbco:id ?ptId ;
      sbco:name ?ptName .
  BIND(?pt AS ?ptDt)
  OPTIONAL { ?pt sbco:writable ?ptWritable . }
  OPTIONAL { ?pt sbco:pointSpecification ?ptSpec . }
  OPTIONAL { ?pt sbco:pointType ?ptType . }
  OPTIONAL { ?pt sbco:gatewayId ?ptGw . }
}
```

---

### Point Details (Full Hierarchy, building-scoped)

**ADT (MATCH):**
```sql
SELECT Floor, Space, Device, Point
FROM DIGITALTWINS
MATCH (Building)-[:hasPart]->(Floor)-[:hasPart]->(Space)<-[:locatedIn]-(Device)-[:hasPoint]->(Point)
WHERE Building.$dtId = '{buildingDtId}'
```

**SPARQL** — building→equipment is joined via the `sbco:floor` string literal (non-`OPTIONAL`, so
equipment without a matching `sbco:floor` is excluded); space/`locatedIn` is `OPTIONAL`:
```sparql
PREFIX sbco: <https://www.sbco.or.jp/ont/>
SELECT ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw
       ?floorDt ?floorId ?floorName ?spaceDt ?spaceId ?spaceName
       ?devDt ?devId ?devName (SAMPLE(?gwRaw) AS ?devGw)
WHERE {
  <{buildingDtId}> sbco:hasPart ?floor .
  ?floor a sbco:Level ; sbco:id ?floorId ; sbco:name ?floorName .
  BIND(?floor AS ?floorDt)
  ?dev a sbco:EquipmentExt ; sbco:id ?devId ; sbco:name ?devName ; sbco:floor ?floorName .
  BIND(?dev AS ?devDt)
  OPTIONAL { ?dev sbco:hasPoint ?gwPt . ?gwPt sbco:gatewayId ?gwRaw . }
  OPTIONAL {
    ?dev sbco:locatedIn ?space .
    BIND(?space AS ?spaceDt)
    ?space sbco:id ?spaceId ; sbco:name ?spaceName .
  }
  ?dev sbco:hasPoint ?pt .
  ?pt a sbco:PointExt ; sbco:id ?ptId ; sbco:name ?ptName .
  BIND(?pt AS ?ptDt)
  OPTIONAL { ?pt sbco:writable ?ptWritable . }
  OPTIONAL { ?pt sbco:pointSpecification ?ptSpec . }
  OPTIONAL { ?pt sbco:pointType ?ptType . }
  OPTIONAL { ?pt sbco:gatewayId ?ptGw . }
}
GROUP BY ?ptDt ?ptId ?ptName ?ptWritable ?ptSpec ?ptType ?ptGw
         ?floorDt ?floorId ?floorName ?spaceDt ?spaceId ?spaceName ?devDt ?devId ?devName
```

---

### Ancestor Chain for Point (Authorization)

**ADT (MATCH):**
```sql
SELECT Building, Floor, Space, Device
FROM DIGITALTWINS
MATCH (Building)-[:hasPart]->(Floor)-[:hasPart]->(Space)<-[:locatedIn]-(Device)-[:hasPoint]->(Point)
WHERE Point.$dtId = '{pointDtId}'
```

**SPARQL** — `OxiGraphHierarchyResolver` first resolves the point DtId by `sbco:id`, then walks the
chain with **explicit multi-hop BGP** (not a recursive property path); the spatial portion is
`OPTIONAL` because SBCO TTL may lack Room / `locatedIn`:
```sparql
PREFIX sbco: <https://www.sbco.or.jp/ont/>
SELECT ?buildingId ?floorId ?spaceId ?devId
WHERE {
  ?dev sbco:hasPoint <{pointUri}> .
  ?dev a sbco:EquipmentExt ; sbco:id ?devId .
  OPTIONAL {
    ?dev sbco:locatedIn ?space .
    ?space a sbco:Room ; sbco:id ?spaceId .
    ?floor sbco:hasPart ?space .
    ?floor a sbco:Level ; sbco:id ?floorId .
    ?building sbco:hasPart ?floor .
    ?building a sbco:Building ; sbco:id ?buildingId .
  }
}
```

> `{pointUri}` is resolved from the business point id:
> `SELECT ?dt WHERE { ?dt a sbco:PointExt ; sbco:id "{pointId}" . }`.

---

### Control Schema by Point Type

ControlSchema has no SBCO equivalent and stays in the `bos:` namespace.

**ADT:**
```sql
SELECT * FROM digitaltwins
WHERE IS_OF_MODEL('dtmi:jp:gutp:bim:ControlSchema;1')
  AND pointType = '{controlSchemaPointType}'
```

**SPARQL:**
```sparql
PREFIX bos: <http://buildingos.gutp.jp/ontology#>
SELECT ?dataType ?enumLabels WHERE {
  ?cs a bos:ControlSchema ;
      bos:pointType "{pointType}" ;
      bos:dataType ?dataType .
  OPTIONAL { ?cs bos:enumLabels ?enumLabels . }
}
LIMIT 1
```

---

## Implementation Notes

### SBCO-specific quirks

- **Room / `locatedIn` may be absent.** The current SBCO TTL may not include `sbco:Room` nodes or
  `sbco:locatedIn`. Space-filtered queries then return empty and space fields are empty; spatial
  joins are therefore `OPTIONAL` in detail/ancestor queries.
- **Building → equipment is a string join.** `sbco:floor` (a string literal on `sbco:EquipmentExt`) is
  matched against a Level's `sbco:name`. This join is **non-`OPTIONAL`** in building-scoped queries —
  making it optional would drop building scoping and return all equipment everywhere. Equipment with a
  missing or mismatched `sbco:floor` will not appear.
  > **Data requirement:** `sbco:floor` (and `sbco:deviceType`, below) must be asserted on the
  > `sbco:EquipmentExt` node — that is where the live queries read them. Asserting them only on
  > `sbco:PointExt` will make building-scoped `ListDeviceDetails` / equipment results empty. (The
  > integration seed `DotNet/BuildingOS.IntegrationTest/Common/Fixtures/SeedData/sbco-sample.ttl`
  > currently places these on `sbco:PointExt`, which does not satisfy the queries above — tracked
  > separately.)
- **Gateway aggregation.** A device may have many points; `(SAMPLE(?gwRaw) AS ?devGw)` with
  `GROUP BY` picks one gatewayId deterministically per device.
- **`deviceType` lives on EquipmentExt** (not PointExt) per the SBCO ontology, and is used by device
  template validation (`DeviceTemplateValidator`).

### 5-Minute Cache

`OxiGraphDigitalTwinDatabase` caches `ListBuildings()` and `ListFloors(null)` for 5 minutes via
`IMemoryCache`, matching the original ADT implementation's cache behaviour.

### Business ID Lookup (Point.GetPoint)

ADT used a sequence of key-name probes (`PointID`, `point_id`, `pointId`). The SBCO implementation
stores the business ID under a single `sbco:id` property, eliminating the multi-probe pattern.

### OxiGraph Docker Image

```bash
docker pull ghcr.io/oxigraph/oxigraph:latest
docker run -p 7878:7878 ghcr.io/oxigraph/oxigraph
```

SPARQL endpoint: `http://localhost:7878`  
Query: `POST http://localhost:7878/query` (Content-Type: `application/x-www-form-urlencoded`, body: `query=...`)  
Update: `POST http://localhost:7878/update` (body: `update=...`)

## customTags によるタグ検索（#332）

SBCO の `customTags` は `map(string → boolean)`、`identifiers` は `map(string → string)` で定義される。
RDF では各エントリを `sbco:KeyBoolMapEntry` / `sbco:KeyStringMapEntry`（リソースまたは blank node）として表現し、
`sbco:key` / `sbco:value` を持たせる。`/resources/search?tag=...` は **`customTags[key] == true`** に一致する
リソースを返す（`false` は明示無効として不一致）。複数 `tag` は **AND**。

### RDF 表現（customTags / KeyBoolMapEntry）

```turtle
@prefix sbco: <https://www.sbco.or.jp/ont/> .
@prefix xsd:  <http://www.w3.org/2001/XMLSchema#> .

<https://www.sbco.or.jp/ont/resource/PT-TAG-1> a sbco:PointExt ;
  sbco:id "PT-TAG-1" ; sbco:name "室温" ;
  sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key "hvac" ;        sbco:value "true"^^xsd:boolean ] ;
  sbco:customTags [ a sbco:KeyBoolMapEntry ; sbco:key "temperature" ; sbco:value "true"^^xsd:boolean ] .
```

`identifiers`（`KeyStringMapEntry`）も同形（`sbco:value` は文字列）。現状の検索対象は `customTags` のみで、
`identifiers` 検索（例 `?identifier.ifcGuid=...`）は将来拡張。

### SPARQL（タグ AND フィルタ）

`ResourceSearchQueryBuilder` は `?dt` を束縛する UNION の外側で、タグ 1 つにつき 1 つの `FILTER EXISTS`
を付与する（値は `"true"^^xsd:boolean` 型付きリテラルで照合。`Prefixes` に `xsd:` を追加済み）。

```sparql
FILTER EXISTS {
  ?dt <https://www.sbco.or.jp/ont/customTags> ?tagEntry0 .
  ?tagEntry0 a <https://www.sbco.or.jp/ont/KeyBoolMapEntry> ;
             <https://www.sbco.or.jp/ont/key> "hvac" ;
             <https://www.sbco.or.jp/ont/value> "true"^^xsd:boolean .
}
```

### API 例

```
GET /resources/search?tag=hvac
GET /resources/search?tag=hvac&tag=temperature      # AND
GET /resources/search?q=temp&type=point&tag=hvac    # q / type / buildingId と併用可
```

| パラメータ | 型 | 説明 |
|---|---|---|
| `tag` | string[] | `customTags[key] == true` の AND 検索（空/空白は無視） |

> **認可との関係**: タグはアクセス制御ではない。処理順は「SPARQL で `q`/`type`/`buildingId`/`tag` 候補取得 →
> `AuthorizedTwinView.SearchAsync` で RBAC フィルタ → 認可済み hit のみ返却」。タグに一致しても read 権限が
> なければ返らない。語彙標準の対応は [standard-mapping.md](standard-mapping.md) を参照。
