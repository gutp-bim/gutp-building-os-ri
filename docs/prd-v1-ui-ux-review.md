# PRD: v1 リリースに向けた UI / UX / デザイン再レビュー（2026-07-16）

- 対象: `web-client/`（Next.js 16 / React 19 / Tailwind CSS 4）
- 位置づけ: #184「v1.0.0 リリース準備トラッキング」の**補完**。外部再評価（2026-07-15, 総合 8.5/10）が
  ペルソナ・情報設計レベルの指摘だったのに対し、本レビューは**全ルート・全コンポーネントのコードレベル走査**で
  「使い勝手・UI・UX・デザイン」の残ギャップを抽出した。
- **本書は抽出とバックログ化のみで実装は含まない。** 各項目は Issue 化して個別に進める。
- 優先度: P0 = v1 宣言前に必須 / P1 = v1 直後の最初のイテレーション / P2 = 製品成熟（#163 と同層）。
- 工数: S = 半日以内 / M = 1〜2日 / L = 3日以上。

---

## 1. 背景と目的

v1.0.0 宣言前の再評価（#184）で P0 とされた項目は #178/#179/#180/#181 Phase 1 の消化により
ほぼ解消し、残る P0 は #156（README スクリーンショット）のみとなった。一方、#184 は**画面の情報設計**
（オペレーターホーム・Gateway 状態・freshness）に焦点があり、次の観点は未走査だった:

1. ルーティング/ナビゲーションの整合（デッドエンド・陳腐化したリダイレクト）
2. エラー・ローディング・空状態の網羅性（グローバル 404/500 を含む）
3. デザインシステムの一貫性（コンポーネント共通化・トークン・依存の健全性）
4. アクセシビリティの実装品質（フォーカス管理・キーボード操作）
5. リリース体裁（デモ残滓・公開ルート・ブランディング）

本 PRD はこの 5 観点で v1 の「初見ユーザーが踏む穴」を塞ぐことを目的とする。

## 2. レビュー方法

- `web-client/src` 全ルート（`(auth)` / `(protected)` 配下 20+ 画面）と共有コンポーネントの精読
- ナビゲーション定義（`src/lib/nav/nav-config.ts`）と実在ルートの突合
- ローディング/エラー/空状態・フォームバリデーション・トースト有無の画面横断調査
- `package.json` 依存と実 import の突合
- 既存バックログ（オープン Issue 14 件 + #184 の P0/P1/P2）との重複排除

## 3. 現状評価（強み — 変更しない）

- **新しい画面群は良質**: オペレーターホーム（#158/#179）、リソースエクスプローラ（`/resources`）、
  admin ユーザー/グループ/権限、platform status/config/settings は一貫した規約
  （`data-testid`・「読み込み中…」・空状態文言・インラインエラー）でテストも厚い。
- **制御の実行フィードバック**（`ControlStatusBar`: executing/success/failed/timeout/cancelled）は
  アプリ内で最も完成度が高い。
- ヘルプ（#149）・オンボーディングツアー（#150）・用語集（#160）の content-as-code 基盤。
- ライトテーマ限定は**意図した決定**（#118, `globals.css` に明記）であり本レビューでは変更しない。

## 4. 課題とバックログ

起票済み Issue との対応:

| 項目 | Issue | 優先度 / 工数 |
|---|---|---|
| UX-1 グローバル 404/エラーページ | #190 | P0 / S |
| UX-2 ログイン後リダイレクト統一 | #191 | P0 / S |
| UX-3 admin 機能のナビ欠落 | #192 | P0 / S |
| UX-4 デモ残滓の除去 | #193 | P0 / S |
| UX-5 UI 基盤統一 + 依存剪定 | #194 | P1 / L |
| UX-6 レガシー詳細ページ移行 | #195 | P1 / M〜L |
| UX-7 通知基盤 + サイレント失敗解消 | #196 | P1 / M |
| UX-8 テレメトリチャート操作性 | #197 | P1 / M |
| UX-9 オーバーレイのフォーカス管理 | #198 | P1 / M |
| UX-10 レスポンシブシェル | #199 | P1〜P2 / L |

### P0 — v1 宣言前に必須（初見ユーザーが必ず踏む穴）

#### UX-1. グローバル 404 / エラーページの不在 + 陳腐化した復帰リンク 【P0 / S】

`src/app/` 直下に `not-found.tsx` / `error.tsx` / `global-error.tsx` が**存在しない**。
`error.tsx`/`not-found.tsx`/`loading.tsx` を持つのは `buildings/[buildingId]` のみ。存在しない
`/floors/xxx`・`/points/xxx`・未知ルートは **Next.js 既定の英語 404**（"This page could not be
found"）に落ちる。しかも既存の `buildings/[buildingId]/error.tsx`・`not-found.tsx` は
「建物一覧に戻る」と `/buildings` へリンクするが、`/buildings` は `/resources` への redirect であり
「建物一覧」画面はもう存在しない。

- 受け入れ基準: 全ルート共通の日本語 404/エラーページ（アプリシェルのトーンに合わせ、`/home` への
  復帰導線付き）。詳細ページ群の error/not-found が現行 IA（`/resources`・`/home`）に整合。
- 根拠: `src/app/`（グローバル境界なし）、`src/app/(protected)/buildings/[buildingId]/error.tsx`

#### UX-2. ログイン後リダイレクトの不整合（#178 の残穴） 【P0 / S】

#178/#185 で「全ロール `/home` ランディング」に統一したが、`(auth)/sign-in/page.tsx:13`
（認証済みユーザーの再訪時）と `auth/oidc-callback/page.tsx:27`（サインイン成功時）は依然
`/buildings` へ push する。`/buildings` → `/resources` の二段 redirect が発生し、**入口によって
着地画面が変わる**。

- 受け入れ基準: サインイン直後・認証済み再訪・ルート `/` の 3 経路すべてが `/home` に着地する。
- 根拠: `src/app/(auth)/sign-in/page.tsx:13`、`src/app/auth/oidc-callback/page.tsx:27`、
  `src/app/page.tsx`（`redirect("/home")`）

#### UX-3. UI から到達不能な admin 機能（ナビ欠落） 【P0 / S】

`/admin/gateways`（#127）・`/admin/oidc-clients`・`/admin/twin` は実装済みだが
`NAV_ITEMS`（`src/lib/nav/nav-config.ts`）に無く（admin はユーザー/グループのみ）、**サイドバーから
到達できない**。パンくず（`breadcrumbForPath`）も解決されず、ワークスペース判定もフォールバックする。
出荷済み機能 3 つが URL 直打ち専用になっている。

- 受け入れ基準: 3 ルートが admin ワークスペースのサイドバー・パンくず・ワークスペース判定に載る
  （権限出し分けは現行ポリシー踏襲）。
- 根拠: `src/lib/nav/nav-config.ts:36-37`（admin 2 項目のみ）、`src/app/(protected)/admin/` 配下 5 ルート

#### UX-4. リリース体裁: 公開デモ残滓の除去 【P0 / S】

1. `src/app/grpc-test/page.tsx` — Greeter gRPC の開発用ハーネス。UI が英語で、かつ
   `middleware.ts` の `matcher` が `grpc-test` を**認証対象から除外**しているため未認証で公開される。
2. `src/app/layout.tsx:17` — メタタイトルが `"Building OS Client Demo App"`（**Demo App**）。
3. `src/components/shell/workspace-placeholder.tsx` —「この画面は準備中です」コンポーネントが
   未使用のまま残存（死コード）。

- 受け入れ基準: grpc-test ページ削除（または dev ビルド限定化 + 認証必須化）、タイトルを製品名称に、
  死コード削除。
- 根拠: `src/middleware.ts:57-59`、`src/app/layout.tsx:17`

### P1 — v1 直後の最初のイテレーション

#### UX-5. UI 基盤の統一: 共有プリミティブ + デザイントークン + 依存の剪定 【P1 / L】

共有 UI コンポーネント層（`components/ui`）が無く、プライマリボタン
（`bg-blue-500 hover:bg-blue-600 …`）等が各画面にコピペされている。モーダルは HeadlessUI
`Dialog`（point 系）と手作りオーバーレイ（ヘルプ/ツアー/アシスタント）と Radix dropdown が併存。
`globals.css` は `--background`/`--foreground` のみでカラートークンが無い。さらに
**shadcn 一式らしき未使用依存が ~30 個**（`@radix-ui/*` の大半、`sonner`、`next-themes`、`vaul`、
`cmdk`、`react-day-picker`、`embla-carousel-react`、`input-otp`、`react-resizable-panels`、
`class-variance-authority` 等）残存し、サプライチェーン面・ビルド面の負債になっている。

- 受け入れ基準: (1) Button/Card/Dialog/FormField 等の共有プリミティブを新設し新画面はそれを使う、
  (2) モーダル基盤を 1 系に統一（フォーカストラップ内蔵のものを推奨 → UX-9 と連動）、
  (3) 主要色・状態色をトークン化（将来のダークテーマ導入コストを下げる）、
  (4) 未使用依存の削除。段階導入可（新規画面から適用 → レガシーは UX-6 で追従）。
- 根拠: `web-client/package.json`、`src/lib/utils`（`cn()` はあるが使用箇所僅少）、
  `src/components/point-control-modal/` ほか

#### UX-6. レガシー詳細ページの新規約への移行 + `/my-resources` の整理 【P1 / M〜L】

`buildings/[id]`・`floors/[id]`・`spaces/[id]`・`devices/[id]`・`points/[id]`・`my-resources` は
旧スタイルのままで、新画面群と**視覚的・挙動的に分裂**している:

- ローディングが `h-32 w-32` の巨大スピナー + `min-h-screen`（シェル内でビューポート丈の空白を作る）
- カードが `<div onClick>` で **キーボード操作不能・スクリーンリーダー非対応**（role/tabIndex なし）
- 罫線・見出しスケールが新規約と不一致（`border-gray-500` 等）
- `/my-resources` は `/resources` と機能重複（旧カードグリッドの残存）

- 受け入れ基準: 詳細ページ群が新規約（テキストローディング or スケルトン・インラインエラー・
  `data-testid`）に揃い、クリック要素が `<a>`/`<button>` 化される。`/my-resources` は
  `/resources` への統合（権限フィルタ表示）または明確な役割定義のうえ再スタイル。
- 根拠: `src/app/(protected)/my-resources/page.tsx`、`src/app/(protected)/floors/[floorId]/` ほか

#### UX-7. 通知基盤 + サイレント失敗の解消 【P1 / M】

トースト/通知システムが無く（`sonner` は未使用のまま）、以下が**ユーザーに何も表示せず**
`console.error` に落ちる: point 詳細の hot データ取得失敗・warm チャート取得失敗・cold CSV
ダウンロード失敗、`point-info.tsx` のクリップボードコピー（成功フィードバックも無し）。
#162（エラー表示統一ポリシー）の**実装面の土台**となる。

- 受け入れ基準: 通知基盤を 1 つ導入（バナー/トーストの使い分けは #162 のポリシーに従う）。
  上記 4 箇所のサイレント失敗が可視化される。回帰テスト付き。
- 根拠: `src/app/(protected)/points/[pointId]/page-component.tsx`（fetchHotData/fetchWarmData/
  handleDownloadCold）、`src/components/point-info.tsx`

#### UX-8. テレメトリチャートの操作性 【P1 / M】

point 詳細の warm チャートは**直近 24h 固定**（`page-component.tsx` でハードコード）で、期間・粒度を
UI から変更できない。バックエンドの統一クエリ（`/telemetries/query` の tier 自動選択 + granularity）
の能力が UI に露出していない。系列色は Recharts 既定の `#8884d8`（紫）でブランド（blue-500）と
不一致。X 軸が `HH:mm` のみで 24h を跨ぐと日付が曖昧。日付レンジ入力は cold ダウンロードモーダルの
`datetime-local` のみで、start < end の検証も無い。

- 受け入れ基準: (1) 期間プリセット（1h/24h/7d/30d + カスタム）と粒度（auto/raw/hour/day）の
  セレクタ、(2) 系列色・軸・凡例の規約化（`dataviz` 的整合）、(3) レンジ入力の start<end 検証。
- 根拠: `src/components/telemetry-warm-data.tsx`、
  `src/app/(protected)/points/[pointId]/page-component.tsx`、
  `src/components/cold-data-download-modal.tsx`

#### UX-9. a11y: オーバーレイのフォーカス管理 【P1 / M】

ヘルプドロワー（`help-drawer.tsx`）・オンボーディングツアー・アシスタントチャットは手作り
オーバーレイで、`role="dialog"`/`aria-modal` が無く、**フォーカストラップ・Esc クローズ・
フォーカス復帰が無い**。E9 評価軸（#159, axe ゲート）は WCAG 自動検査のみでフォーカス管理までは
検出しない。HeadlessUI `Dialog` を使う point 系モーダルは対応済みなので、UX-5 のモーダル統一と
同時に解消するのが合理的。

- 受け入れ基準: 3 オーバーレイがダイアログセマンティクス + フォーカストラップ + Esc + 復帰を持つ。
  Playwright にキーボード操作の回帰テストを追加（E9 の keyboard 指標と接続）。
- 根拠: `src/components/help/help-drawer.tsx`、`src/components/onboarding/`、
  `src/components/assistant/`

#### UX-10. レスポンシブシェル（タブレット/モバイル） 【P1〜P2 / L】

`AppShell` は `flex h-screen` + 固定 `w-56` サイドバーのデスクトップ専用で、ハンバーガー/
ドロワー化が無い。`/resources` の 2 ペイン（`w-1/3 min-w-[18rem]` aside）も狭幅でスタックしない。
現場巡回中の設備管理者がタブレットで「要対応一覧 → point 詳細」を見る利用像は自然であり、
v1 直後の優先度は高い。ただし全画面のモバイル最適化は不要（シェル + home + resources + point 詳細
に限定した段階導入とする）。

- 受け入れ基準: md 未満でサイドバーがドロワー化、home/resources/point 詳細が縦スタックで崩れない。
  E2E にビューポート回帰を追加。
- 根拠: `src/components/shell/app-shell.tsx`、`src/components/shell/sidebar.tsx`

### 表記・その他（P1 に相乗り / 単独 Issue 化しない）

- **英語混在**: 権限エディタのリソース種別ドロップダウン・リソースツリーの type バッジが生 enum
  （`device`/`building`…）表示 → UX-6 で日本語ラベル化。UI 全体の多言語化は #163 スコープのまま。
- **チャート以外の細部**: `point-info.tsx` の `border-gray-500` 罫線、見出しスケールの不一致
  → UX-5/UX-6 の規約適用で解消。

## 5. 既存バックログとの対応（重複しないこと）

| 既存 Issue | 関係 |
|---|---|
| #184 v1.0.0 メタ | 本 PRD の P0（UX-1〜4）を v1 ブロッカーとして追補する位置づけ |
| #156 README スクショ | 独立（本 PRD はアプリ内 UI のみ） |
| #162 操作フィードバック統一 | ポリシー策定は #162。UX-7 は通知**基盤**とサイレント失敗解消で土台を提供。制御の確認ダイアログも #162 スコープ（本 PRD では重複起票しない） |
| #181 Phase 2 Gateway 実状態 | 独立（本 PRD は対象外） |
| #182 / #183 freshness | 独立（データ面。UI 面は消化済み） |
| #159 E9 / Playwright | UX-9・UX-10 の受け入れ基準が E9 指標（keyboard/axe）に接続 |
| #163 P2 傘 | ダークテーマ・UI 多言語化・障害時縮退表示は引き続き #163。UX-5 のトークン化はその前提整備 |
| #154 vite/vitest メジャー | UX-5 の依存剪定と同時に行うと効率的（相互参照） |

## 6. スコープ外

- ダークテーマの実装(#118 の決定を維持。UX-5 はトークン化までで、テーマ追加はしない)
- UI 多言語化(i18n フレームワーク導入)・アラーム/エネルギー画面 — #163
- 制御フローの確認ダイアログ・権限不足の説明表示 — #162
- バックエンド API の変更を要する項目(Gateway 実状態 #181 Phase 2、freshness #182/#183)

## 7. 推奨着手順

| 順 | 項目 | 理由 |
|----|------|------|
| 1 | UX-2 / UX-4(リダイレクト・デモ残滓) | 各 S。v1 の体裁を最速で整える |
| 2 | UX-1(グローバル 404/エラー) | S。初見ユーザーの必踏経路 |
| 3 | UX-3(admin ナビ欠落) | S。出荷済み機能の可視化 |
| 4 | UX-7(通知基盤) | #162 のポリシー決定と並走可能 |
| 5 | UX-5 → UX-6 → UX-9 | 基盤 → レガシー移行 → a11y の順で積み上げ |
| 6 | UX-8 / UX-10 | 独立して進行可能 |
