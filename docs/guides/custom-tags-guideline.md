# customTags 運用ガイドライン

`sbco:customTags` は、リソース（Point / Device / Space など）に運用者が自由に付けられる**横断的なラベル**です。
`/resources` の検索（`tag` の AND 絞り込み）とタグ候補表示に使われます。

> **原則: 構造は Twin に、運用上のラベルだけをタグに。**
> 場所・機器・単位・Gateway のように Twin が構造として持てる情報をタグにすると、Twin とタグの二重管理になり、
> 片方だけが更新されて食い違います。

## 入れないもの（Twin の構造化情報）

| 情報 | 本来の置き場所 | タグにしてはいけない例 |
|---|---|---|
| フロア・部屋・建物 | トポロジ（`sbco:hasPart` / `sbco:locatedIn`） | `3F`, `floor=3F`, `building-a` |
| 機器の種別 | `sbco:deviceType` | `AHU`, `VAV` |
| 計測の種類 | `sbco:pointType` | `temperature`, `co2` |
| 単位 | `sbco:unit` | `degC`, `ppm` |
| Gateway | `sbco:gatewayId` | `gw-001` |

これらは `/resources` の facet（機器・計測・単位・Gateway）として絞り込めます。タグにする必要はありません。
鮮度（新鮮 / 鮮度切れ / 欠測）とアラームも facet で絞り込めます（ポイントを対象にしているとき）。
`sbco:floor` / `sbco:building` のようなリテラルは**メタデータであり、何かを配置しません**
（トポロジだけが場所を決めます）。

## 入れるもの（運用上の横断ラベル）

構造では表せず、運用の都合で束ねたい切り口です。

- 重要度・扱い: `critical`, `energy-saving-target`, `experimental`
- 契約・責任: `tenant-a`, `owner-facilities`
- 作業状態: `maintenance-required`, `commissioning`

## 命名規則

- **小文字・ハイフン区切り**（`energy-saving-target`）。大文字小文字や区切り文字の揺れを作らない。
- **1 タグ 1 概念**。`tenant-a-critical` のような複合は避け、`tenant-a` と `critical` の 2 つにする。
  検索は AND なので、組み合わせは検索側で作れます。
- **新規作成の前に候補を確認する。** タグ入力に既存タグが候補表示されます（`temp` と `temperature`
  のような揺れの防止）。
- 値の無いフラグとして使う（`customTags[key] == true`）。値付きの情報（`floor=3F`）は入れない。

## 権限について

タグは**閲覧権限の範囲内のリソース**にだけ集計・候補表示されます。タグ候補の件数は、権限のない
リソースを含みません。Group 管理専用ロール（group-manager）にはタグは表示されません。
建物への付与だけで配下のリソースを読めるユーザーは、候補が少なく表示されることがあります
（候補は各リソースへの直接の付与で数えます。多く出ることはありません）。

## 関連

- [リソース管理](resource-management.md)
- `web-client/src/lib/help/content.ts`（アプリ内ヘルプの用語集に同じ要約があります）
