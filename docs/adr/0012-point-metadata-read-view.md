# ADR-0012: native boundsを含まない明示的point metadata読取りview

- Status: Proposed（2026-10-28、Testbed #3の方針をユーザー承認。製品レビュー前）
- 関連: ADR0011 / #610、takashikasuya/building-os-e2e#3

## 決定案

`GET /api/v1/point-metadata?buildingDtId=...`を追加する。building-scopedの
point-detailsと同じ認証/認可・404・設備関係/ControlSchema resolverを使い、
明示的DTOでpointのdtId/id/name/unit/writableだけとbuilding/floor/space/device、
ControlSchemaを返す。raw minPresValue/maxPresValue、内部representation flagは返さない。
ControlSchemaのminValue/maxValueは制御契約でありraw boundsとは別なので保持する。
DB/RDFは変更せず、元の小数も保持する。投影は既存pointを変更しない。

既存points/point-detailsとint32/409契約は変更しない。新viewはbounds欠損の代用ではなく、
最初からraw boundsを契約に持たない別view。部分点リストや丸め/推測は行わない。
BDEが明示opt-inでstate単位解決に使う。非対応RIの404時は従来viewへfallbackしない。

## 互換性・受入

- 旧consumer+新RI: 既存routeと409/401/403/404を保持。
- opt-in consumer+新RI: 小数boundsを持つ点もID/unit/設備関係を正確に読める。
- opt-in consumer+旧RI: 404/未対応を明示し単位未解決として拒否。
- 匿名/許可外buildingを拒否。新viewから点数やIDを漏らさない。
- 実OxiGraph/JWT/RESTと通常BDE clientで35点と単位換算を横断検証する。
- Safetyの書込権限・hard limits・provenance承認を変更しない。

Rollback: consumerのopt-inを外し旧RIへ戻す。小数点を含む旧viewの409は成功扱いしない。
新APIの利用開始は両側レビュー/固定SHAの受入後とし、Testbed単独で完了させない。
