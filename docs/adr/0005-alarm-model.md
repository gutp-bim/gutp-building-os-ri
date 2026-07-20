# 運用者向けアラーム（値閾値の逸脱）のデータモデルと評価方式

- **Status**: Proposed（設計先行。#158 Phase 2。実装は maintainer が下記 Open Decisions を承認後に着手）
- **関連**: #158（オペレーターモード。Phase 1 = 鮮度/欠測は実装済み #176/#233）、#148（SettingsRegistry）、
  #183（point 別 expected interval 鮮度閾値）、#153（`bos:minValue`/`maxValue` 制御バウンド）、#230（gateway 状態）、
  #162（通知ポリシー）

## Context

設備管理者の日常タスクのうち「**異常がある設備を知る**」に対応する画面が無い（#158 の表）。Phase 1 で
**鮮度切れ/欠測**（データが届いているか）は出せるようになったが、これは*データ到着*の異常であって
**値そのものの異常（閾値逸脱）**ではない。Phase 2 は「アラーム概念の導入」で、issue 本文は
**「閾値定義が必要 — `/platform/settings` の SettingsRegistry 拡張で対応可能か要設計」**と明記している。本 ADR は
その設計判断（閾値の置き場所・評価方式・UI・段階）を確定する。

### 既存資産（調査結果、file:line）

- **SettingsRegistry（#148）はスカラー・グローバル限定**。`SettingType` は `{ Boolean, Number, String }` のみで
  JSON/構造化型は無く、キー→値のフラットなグローバル allowlist（`SettingsRegistry.cs:8-43`,
  `SettingDefinition.cs:5-12,29-34`）。**point/unit ごとの次元を持てない**。既存キーは
  `telemetry.staleThresholdSeconds` / `telemetry.staleIntervalMultiplier`（鮮度）等の全体既定のみ。
  → **結論: SettingsRegistry だけでは per-point アラーム閾値は表現できない**（issue の問いへの答え）。
  String に JSON を押し込むことは可能だが、スキーマ検証も per-point 次元も無く、破綻する。
- **鮮度/要対応の仕組み**（Phase 1）は「異常 point」を出す唯一の既存面。純関数 `classifyPointFreshness`
  （`web-client/src/lib/telemetry/freshness.ts:57-81`）が `fresh|stale|missing` に分類し、
  `buildAttentionList`（`web-client/src/lib/home/aggregate.ts:31-59`）が worst-first で一覧化。
  **値閾値ではなく到着遅延で判定**している。アラームはこの隣に生やすのが自然。
- **現在値の一括読み取り** `POST /telemetries/query/batch-latest`（`TelemetryController.cs:220-277`, 上限 500,
  `Task.WhenAll` 並行 fan-out）が既にある。**アラーム評価器はこれを再利用**して現在値を1往復で取れる。
- **per-point の数値バウンドは ControlSchema に既存**だが用途が違う: `ControlSchema.MinValue`/`MaxValue`
  （`bos:minValue`/`bos:maxValue`, #153, `ControlSchema.cs:20-24`）は**制御入力（setpoint 書き込み）の有効範囲**で、
  「正常運転レンジ」＝アラーム閾値とは別物。twin には `sbco:unit`（単位）と `sbco:interval`（鮮度用）もある。
- **アラーム/イベント/値閾値/イベント永続化のドメインモデルは一切存在しない**（backend/frontend とも net-new）。
  既存の "alert" は UI 部品（toast/banner）と Prometheus/Grafana のインフラ監視で、設備アラームではない。

## Open Decisions（maintainer 承認が要る論点）

### D1. 閾値の置き場所

| 案 | 内容 | 評価 |
|----|------|------|
| **A. SettingsRegistry 拡張** | 既存 registry に閾値キーを足す | ❌ スカラー・グローバルのみ。per-point 不可。**単独では不採用**（全体既定の受け皿としてのみ有効） |
| **B. Twin（OxiGraph）per-point**（推奨） | `bos:alarmHigh`/`bos:alarmLow`（任意で `bos:warnHigh`/`warnLow`）を point に追加し、ControlSchema と同様に `PointDetail` へ projection | ✅ 「twin が point メタデータの正本」という既存方針に整合。per-point/per-device が自然。twin admin（#322）で編集可。seed で配布可 |
| **C. 新リレーショナルテーブル `alarm_rule`** | per-point / per-unit ルールを EF + CRUD API で | ✅ 柔軟（unit テンプレート・有効無効・hysteresis）だが、テーブル+migration+CRUD UI が要り重い。twin 再seed 不要で運用者編集したい時に価値 |

**推奨: B（twin per-point 閾値）を Phase 2a の受け皿**にする。全体/unit 既定は SettingsRegistry の
スカラー既定（例 `alarm.defaultEnabled`）で補う**ハイブリッド**。C（運用者編集の per-unit テンプレート）は
将来 Phase 2c として、編集 UX 要件が固まってから。**ControlSchema.Min/Max は流用しない**（制御バウンド≠アラーム
閾値。ただし閾値未設定 point の暫定フォールバック既定として参照するかは D2 の議論に含める）。

### D2. 評価方式（派生 vs. 常駐評価器＋イベントストア）

| 案 | 内容 | 評価 |
|----|------|------|
| **A. Derived-on-read**（推奨 / Phase 2a） | 鮮度と全く同じ。alarm 一覧/operator-home が batch-latest で現在値を取り、純関数 `classifyPointAlarm(value, thresholds)` で `ok/warn/critical` 判定。**イベント永続化なし・常駐worker なし・状態なし** | ✅ 鮮度の先例に完全整合。安全・低リスク・即出せる。スナップショット判定なので flapping/hysteresis 不要。❌ 履歴・ack・通知は持てない |
| **B. 常駐評価器＋イベントライフサイクル**（Phase 2b+） | ConnectorWorker の BackgroundService が validated telemetry を購読 → 閾値評価 → **アラームイベント（raise/clear + ack 状態）**を新テーブルに永続化 | ✅ 履歴・確認応答・通知・ストーム制御が可能な「本物」。❌ 状態機械・重複排除・storm 制御・ack UI が要り大規模 |

**推奨: Phase 2a = Derived-on-read**（鮮度と対称、イベントのデータモデル不要、`#158 Phase 1` と `#230 Phase 1`
で有効だった「軽量スライス先行」パターン）。**Phase 2b = イベントライフサイクル**（raise/clear/ack/履歴 + 通知は
#162 ポリシーに接続）を、閾値モデルと UX が実運用で検証できてから後続で。

### D3. 閾値のセマンティクス（Phase 2a 最小）

- **high/low の静的リミット**（`alarmHigh`/`alarmLow`）+ 任意で warn/critical 2段（`warnHigh`/`warnLow`）。
  単位は `sbco:unit` を表示に流用。boolean/enum point は Phase 2a 対象外（数値 point のみ。将来 enum の
  「異常状態コード」を D1-C のルールで扱う）。
- **hysteresis/deadband は Phase 2a では不要**（derived-on-read はスナップショット判定なので発振しない）。
  Phase 2b の常駐評価器で raise/clear を持つ時に導入。
- 閾値未設定 point は **`unknown`（評価対象外）**として静かに除外（鮮度の `missing` とは別軸）。

### D4. UI

- operator-home の **要対応リスト（`buildAttentionList`）を拡張**し、値逸脱アイテムを stale/missing と並べて
  worst-first（critical > warn > missing > stale の順を推奨）で出す。サマリカードに「異常」件数を追加。
- 各行は既存同様 `/points/{pointId}` へ導線。#230 の gateway 状態、#162 の通知ポリシーと表示軸を分ける
  （到着=鮮度 / 接続=gateway / 値=アラーム）。専用「アラーム一覧」ルートは Phase 2b（履歴・ack を持ってから）。

## Decision（推奨サマリ、承認待ち）

1. 閾値 = **twin per-point（`bos:alarmHigh/Low` [+ warn]）**、全体既定は SettingsRegistry スカラー。SettingsRegistry
   単独拡張は不採用（issue の問いへの回答）。ControlSchema.Min/Max は流用しない。
2. 評価 = **Phase 2a は derived-on-read**（鮮度対称の純関数 + batch-latest 再利用、イベント永続化なし）。
   **Phase 2b で常駐評価器＋イベント（raise/clear/ack/履歴）**。
3. UI = 要対応リスト拡張（値逸脱を worst-first で併記）。専用アラーム画面は Phase 2b。
4. 段階: **2a（静的閾値 + 派生判定 + 一覧）→ 2b（イベントライフサイクル + 通知 + 履歴）→ 2c（per-unit ルール
   テーブルと運用者編集 UI, D1-C）**。各段は独立に価値を出し独立にレビュー/マージできる。

## Consequences

- Phase 2a は**新しい永続データモデルゼロ**（twin プロパティ追加 + 純関数 + 既存 batch-latest + UI）で、鮮度と
  同じ検証容易性・低リスク。実機なしでも純関数 + コンポーネントテストで十分カバーできる。
- twin に閾値を置くと **seed で配布**でき、既定サンプル twin（#124）にデモ閾値を載せられる。運用者がUIから
  即編集したい要件が出たら D1-C（Phase 2c）で解く（twin admin 経由編集は #322 で可能）。
- Phase 2b のイベントストアは #162 通知ポリシー・#158 の「アラーム一覧」・将来のエスカレーションと接続する土台。
  ここで初めて ack/履歴/storm 制御という本質的複雑さが入るので、2a で UX と閾値モデルを固めてから着手する。

## Open Questions（実装前に確定）

1. **D1 の採否**（B twin per-point / ハイブリッド / いつ C を足すか）。
2. **D2 の段階**（2a を先に出す方針でよいか、最初から 2b イベントを設計するか）。
3. 閾値プロパティ名（`bos:alarmHigh/Low` + `warnHigh/Low`）と、単位整合の扱い。
4. worst-first の重み付け（critical/warn/missing/stale の序列）。
5. 未設定 point の扱い（`unknown` 除外 / ControlSchema.Min/Max フォールバック の可否）。

## 参照

- ADR-0004（#230, 軽量スライス先行 → 後続の段階設計の先例）、`docs/oss-sla-freshness.md`(#183 鮮度閾値)。
- コード: `SettingsRegistry.cs` / `SettingDefinition.cs`（スカラー限定の根拠）、`freshness.ts` / `aggregate.ts`
  （派生判定 + 要対応リストの先例）、`TelemetryController.cs`（batch-latest 現在値読み）、
  `ControlSchema.cs` / `OxiGraphOntology.cs`（既存 per-point バウンドと twin プロパティ）。
