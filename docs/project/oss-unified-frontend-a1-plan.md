# A-1 統合フロントエンド基盤 — 詳細計画 + UX レビュー（#141）

## 1. 現状 UX レビュー（既存 UI の課題）

| 観点 | 現状 | 課題 |
|---|---|---|
| アプリ分割 | web-client(3000) と admin-console(3001) が別 URL・別ログイン | 両ロールを持つ人が**2つのアプリを行き来**。コンテキストが切れる |
| シェル/ナビ | `(protected)` に**共有レイアウト/ナビが無い**（各ページが孤立、`page.tsx` 直置き） | グローバルナビ無し＝**現在地・移動手段が不明確**。IA が無い |
| ロール表現 | フロントに**ロール/権限処理が皆無**。claims も未公開（`auth-context` は isAuthenticated/signOut のみ） | 権限で出し分けできない。見えてはいけないものを隠せない（UI 側） |
| 認可の二重化 | — | UI ガードと API 認可の責務分離が未定義 |
| サインアウト/ユーザー表示 | 散在 | 誰でログイン中か・抜ける導線が一貫しない |

### UX 原則（本基盤の指針）
1. **1 アプリ・1 ログイン**。ロールで見えるものを変える（出し分けは利便性、認可は API が担保）。
2. **常時見えるグローバルシェル**（ヘッダー + サイドバー）。現在地と移動手段を明示。
3. **ワークスペースという単位**で役割を切り替え、「今どの帽子か」を常時可視化。
4. **権限が無い導線は出さない**（淡色無効化ではなく非表示）。ただし最終防御は API。
5. キーボード/スクリーンリーダー配慮（Radix UI 準拠）。日本語一次。

## 2. 情報アーキテクチャ（ワークスペース）

3 ワークスペース。ロール（`building_os_role` / permissions）で可視性を決定。

| Workspace | 対象ロール | ナビ項目（A-1 は枠とプレースホルダ、中身は後続 Issue） |
|---|---|---|
| **operator**（建物運用） | viewer / operator / admin | Buildings, Floors, Spaces, Devices, Points, My Resources（既存画面） |
| **admin**（管理） | admin | Users, Groups（#143 で移植）|
| **platform**（運用） | admin | System Status（#146/B-3）, Config（#147/C-1）|

- 単一ロールなら該当 WS のみ表示、WS 切替は出さない or 1 つ。
- 複数ロール（例 admin）は WS 切替を表示。**現在の WS をヘッダーに常時表示**。

## 3. インタラクション設計（シェル）

```
┌───────────────────────────────────────────────────────────┐
│ Header: [Building OS] [WS▼ operator] ……… [user ▼ / signout]│
├──────────┬────────────────────────────────────────────────┤
│ Sidebar  │  page content                                   │
│ (WS の   │                                                 │
│  ナビ)   │                                                 │
└──────────┴────────────────────────────────────────────────┘
```

- **WorkspaceSwitcher**（ヘッダー）: 保持ロールが許可する WS のみ列挙。選択で当該 WS の既定ページへ遷移。1 つしか無ければ静的ラベル表示。
- **Sidebar**: 現在の WS のナビ項目のみ。各項目は権限で更に出し分け可能。
- **UserMenu**: 表示名 + サインアウト。
- レスポンシブ: モバイルはサイドバーをドロワー化（A-1 は最小、デスクトップ優先）。

### ルーティング方針
- 各 WS のページは実セグメント配下に置く: `/operator/*`, `/admin/*`, `/platform/*`（route group `()` は URL に出ず切替の同定が曖昧なので、**実パス prefix** を採用）。
- 既存 `(protected)` 配下のページは段階移行（A-1 では既存パスを残し、シェルを被せる）。`/` は保持ロールの既定 WS にリダイレクト。
- **サーバサイドガード**: `middleware.ts` で cookie の JWT を decode し、WS パスへのアクセスをロールで判定（未許可は既定 WS へ）。署名検証はしない（UX ガード。真の認可は API）。

## 4. 実装スコープ（A-1 = 基盤のみ）

**やる**: テスト基盤、ロール/WS ドメイン（純関数・TDD）、claims 公開、AppShell/Sidebar/Header/WorkspaceSwitcher、ナビ構成（content-as-code）、middleware ガード、既存ページへの非破壊配線、UX ドキュメント。

**やらない（後続）**: admin/platform の実画面（#143/#146/#147）、admin-console の物理統合・撤去（#143）、Keycloak 単一クライアント化の realm/infra 変更（ドキュメント化のみ）。

### TDD 対象（純ロジック優先）
- `lib/auth/claims.ts` — JWT decode → `{ role, permissions[] }`（不正/欠落の degrade）
- `lib/auth/workspaces.ts` — `workspacesForRole(role)`, `defaultWorkspace(role)`, `canAccessWorkspace(role, ws)`
- `lib/nav/nav-config.ts` + `visibleNavItems(ws, perms)` — ナビ出し分け
- コンポーネント: `WorkspaceSwitcher` のロール別表示（@testing-library）

### 変更ファイル（予定）
| ファイル | 内容 |
|---|---|
| `web-client/vitest.config.ts`, `vitest.setup.ts`, package.json | テスト基盤 |
| `src/lib/auth/claims.ts` (+test) | claims decode |
| `src/lib/auth/workspaces.ts` (+test) | WS ドメイン |
| `src/lib/nav/nav-config.ts` (+test) | ナビ構成 |
| `src/lib/auth/oidc-auth-provider.tsx` | claims を context に公開 |
| `src/components/shell/*` | AppShell/Header/Sidebar/WorkspaceSwitcher/UserMenu |
| `src/app/(protected)/layout.tsx` | シェル配線（非破壊） |
| `src/middleware.ts` | WS ロールガード |

## 5. 検証
- `yarn typecheck` / `yarn lint` / `yarn build` / `yarn test`（Vitest）
- 専門エージェントによるコードレビュー → 指摘反映
- PR 発行（マージしない）
