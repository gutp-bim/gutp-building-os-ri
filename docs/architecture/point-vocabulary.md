# Point の語彙（specification / type / writable / unit / scale / interval）

`GET /api/v1/points*` が返す `Point` の属性のうち、値の意味が利用側の判断に直結するものについて、
**Building OS が実際にどう扱っているか**と、twin を作る側が従うべき語彙をまとめる（#483）。
SBCO / Brick / REC との対応は [`standard-mapping.md`](standard-mapping.md) を参照。

> **前提:** ここに挙げる値はどれも twin（OxiGraph）のリテラルをそのまま返したものである。Building OS は
> 取り込み時にも読み取り時にも、値を検証・正規化・変換しない（例外は各節に明記）。語彙は twin を作る側
> （ポイントリスト CSV → TTL のツール、管理画面の取り込み）が守る約束であり、Building OS が強制する
> ものではない。

## 早見表

| 属性 | SBCO 述語 | 型 | Building OS の扱い | 未設定 |
|---|---|---|---|---|
| `specification` | `sbco:pointSpecification` | 文字列 | 表示・絞り込みのみ。分岐しない | あり得る |
| `type` | `sbco:pointType` | 文字列 | 表示・絞り込みのみ（web では `kind`） | あり得る |
| `writable` | `sbco:writable` | 真偽 | `false` なら制御を拒否（admin も） | あり得る（拒否しない） |
| `unit` | `sbco:unit` | 文字列 | 表示のみ。変換しない | あり得る |
| `scale` | `sbco:scale` | 数値 | 表示と健全性のしきい値判定でだけ `raw × scale` | あり得る（1 とみなす） |
| `minPresValue` / `maxPresValue` | `sbco:minPresValue` / `sbco:maxPresValue` | 整数 | 制御 UI の範囲表示の予備のみ。scale 前（raw） | あり得る |
| `interval` | `sbco:interval` | 数値（秒） | 鮮度（stale）判定のしきい値 | あり得る（システム既定） |

どの属性も SPARQL では `OPTIONAL` で読み（`OxiGraphDigitalTwinDatabase` の `PointOptionals`）、数値に
変換できない値は `null` になる。

## 1. `specification` と `type`

### どちらに何を入れるか

- **`specification`（`sbco:pointSpecification`）＝ 点の役割。** 次の 4 値のどれかを入れる。
  | 値 | 意味 | 典型 |
  |---|---|---|
  | `Measurement` | センサの計測値（観測） | 室温、CO2、電力量 |
  | `Setpoint` | 設定値（書込先になる目標値） | 室温設定、給気温度設定 |
  | `Command` | 機器への指令（発停・モード・開度など） | 発停指令、ダンパ開度指令 |
  | `Status` | 機器の状態（運転状態・警報など） | 運転/停止、故障 |
- **`type`（`sbco:pointType`）＝ 物理量や対象の種別。** `Temperature`、`Humidity`、`CO2`、`Power`、
  `Energy`、`Pressure`、`Airflow`、`FanSpeed`、`Position`、`Occupancy`、`Lighting`、`Alarm` などの
  自由な語。区切りは CamelCase を推奨する（既存の twin には `Gas Flow` のような空白入りもある）。

### 点種別（sensor / setpoint / command / status）の決め方

**Building OS は点種別を導出しない。** `specification` / `type` で分岐するコードは無く、表示・検索・
並べ替えに使うだけである。利用側が点種別を決めるときは、次の順に読むことを推奨する。

1. `specification` が上の 4 値のどれかなら、それをそのまま点種別とする（大文字小文字は完全一致）。
2. それ以外・未設定なら「判別不能」とする。名前や値の統計から推定する場合は、推定であることを
   利用側で区別して扱う（制御対象の選定には使わない）。

`type` に `Setpoint` や `Status` が入っている twin もある（demo fixture など）。これは種別ではなく
物理量の欄に役割を書いてしまった例で、点種別の判定には `specification` を優先する。

### 現状の値（参考）

| twin | `specification` | `type` |
|---|---|---|
| `fixtures/demo/twin.ttl`（1,198 点） | Measurement 1,001 / Setpoint 81 / Status 57 / Command 51、未設定 8 | 14 種 |
| `fixtures/e2e/twin.ttl` | 未設定（すべて） | Occupancy / Lighting / Chiller など |

## 2. `writable` と制御

`POST /api/v1/points/{id}/control` が書込を受け付けるのは、次を**すべて**満たすときである
（詳細は [`oss-control-safety.md`](oss-control-safety.md)）。

1. `writable` が `false` でない（`AuthorizedTwinView.CanWritePointAsync`。admin も拒否する）
2. 呼び出し側が admin か、その Point に `write` 権限がある
3. Point の所属機器の Gateway が、API から制御できる接続方式（`hono` / `bacnet-sim`、シミュレータの
   `simulated`）に解決できる。それ以外（`kandt` など）や Gateway の無い点は 400
   （`ControlTypeResolver`。`writable=false` もここで再確認する）
4. ControlSchema（`bos:dataType` / `bos:enumLabels` / `bos:minValue` / `bos:maxValue`）で値を検証できる。
   検証できない（スキーマが無い・型が不明など）ときは、`CONTROL_SCHEMA_FAILURE_POLICY=allow`（既定）なら
   検証せずに送り、`deny` なら拒否する
5. Gateway が接続中である（未接続なら 503）

したがって：

- **`writable=true` は必要条件でも十分条件でもない。** 未設定（`null`）の点は 1. を通る。書けるかどうかは
  上の 1〜5 の組み合わせで決まる。
- **`writable=false` は、権限やスキーマに関係なく書込を禁止する唯一の宣言である。** 書いてはいけない点
  （センサ値・状態値）には必ず `false` を入れる。
- 書込先にする点（`Setpoint` / `Command`）には `writable=true` と ControlSchema（少なくとも `bos:dataType`）
  を入れる。範囲を持つ点は `bos:minValue` / `bos:maxValue` も入れる（制御値の範囲の**唯一の正本**）。
- **値の表記:** `true` / `false`（小文字）で書く。読み取りは文字列 `"true"` との完全一致で判定するため、
  `"1"` や `"TRUE"` は `false` として扱われ、制御が拒否される。

## 3. `unit`

**自由な文字列で、Building OS は正規化も換算もしない。** web の表示では、QUDT の IRI と一部の別名
（`degC` → ℃ など）を表示用ラベルに置き換える（`resolveUnitLabel`）だけで、値は変えない。

twin を作る側は、次の表記に揃えることを推奨する（UCUM の case-sensitive 表記に近い、既存 twin で
使われている綴り）。

| 量 | 表記 |
|---|---|
| 温度 | `degC` |
| 相対湿度 | `%RH`（`%` は割合一般に使う） |
| 濃度 | `ppm` |
| 電力 / 電力量 | `W`, `kW` / `kWh` |
| 圧力 | `Pa` |
| 風量・流量 / 体積 | `m3/h` / `m3` |
| 熱量 | `MJ` |
| 電流 | `A` |
| 日射 | `W/m2` |
| 風速 | `m/s` |
| 降水量 | `mm` |

複数の点をまたいで物理量を計算する利用側は、単位が上の表記であることを確かめ、未設定や表外の値の
点は計算から外すこと。

## 4. `scale` と `minPresValue` / `maxPresValue`

- **向き:** `工学値 = 保存値（raw）× scale`。未設定は 1 とみなす。
- **どこで掛けるか:** Building OS は**保存時にも API の応答でも掛けない**。テレメトリ（`/telemetries/*`）は
  ゲートウェイが送った raw 値のまま返す。`scale` を掛けるのは次だけである。
  - web の Point 詳細の最新値表示（`telemetry-hot-data.tsx`）と健全性パネル（`point-health-panel.tsx`）
  - サーバの健全性分類で、警報・注意のしきい値（`alarmHigh` などは**工学値**）と比べるとき
    （`TelemetryHealthController`）
  - 履歴グラフと制御値には掛けない。**制御で送る値は raw** である。
- **`minPresValue` / `maxPresValue`:** BACnet 機器の raw の範囲（scale を掛ける前）。制御 UI で
  ControlSchema の `bos:minValue` / `bos:maxValue` が無いときの範囲表示の予備としてだけ使い、制御値の
  検証には使わない。
- 利用側で工学値を扱うときは、テレメトリの値に自分で `scale` を掛ける。

> **ポイントリスト CSV の `min_pres_value` / `max_pres_value` 列について:** E2E 用の取り込みツール
> （`Tools/e2e-performance/seed_from_csv.py`）は、この 2 列を `sbco:minPresValue` / `maxPresValue` では
> なく **`bos:minValue` / `bos:maxValue`（制御範囲の正本）** に書く。制御範囲を持たせたくない点では
> 空にすること。

## 5. `interval`

**期待する送信間隔（秒）。** データ健全性（`GET /api/v1/telemetry/health`、#183）は、最後の受信から
`interval × 倍率`（既定 3、`/platform/settings` の `telemetry.staleIntervalMultiplier`）を過ぎた点を stale と
判定する。`interval` が未設定・0 以下・数値でない点は、システムの既定しきい値（`StaleThresholdSeconds`）
を使う。

## 6. 未設定があり得るか

すべての属性は未設定になり得る（twin に述語が無い、または数値に変換できない）。主な理由は次のとおり。

- ポイントリスト CSV の該当列が空（取り込みツールは空の列を述語にしない。`writable` だけは常に書く）
- twin を別の経路（手書きの TTL、REC 語彙の twin）で作った
- E2E 用の fixture のように、確認に必要な属性だけを持たせた twin

利用側は、未設定を「既定値」ではなく「不明」として扱うこと（`writable` の未設定は、Building OS では
書込を禁止しない点に注意）。

## 関連

- [`standard-mapping.md`](standard-mapping.md) — SBCO / bos: と Brick / REC の対応
- [`oss-control-safety.md`](oss-control-safety.md) — 制御の安全策（ControlSchema、失敗時の方針）
- [`../guides/onboarding-e2e-gateway.md`](../guides/onboarding-e2e-gateway.md) — ポイントリスト CSV の列
