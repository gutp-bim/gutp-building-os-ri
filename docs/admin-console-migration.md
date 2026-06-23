# admin-console → 統合アプリ移植 / 段階的撤去計画（#143）

`admin-console`（別 Next.js アプリ、port 3001）の users/groups 管理を、統合アプリ `web-client` の
`(admin)` ワークスペースへ移植する。移植は機能単位の **increment** で進め、各 increment が web-client 側で
完結したら admin-console の対応画面を撤去する（一気の置き換えはしない）。

## 移植スコープと進捗

| 画面 | admin-console | web-client `(admin)` | 状態 |
|---|---|---|---|
| ユーザー一覧 | `users/page.tsx` | `admin/users/page.tsx` | ✅ 移植（読み取り、#143 第1弾） |
| ユーザー詳細（読み取り） | `users/[id]/page.tsx` | `admin/users/[id]/page.tsx` | ✅ 移植（属性 + 権限表示） |
| ユーザー権限の追加/削除 | `users/[id]`（PermissionPicker） | `admin/users/[id]/page.tsx` | ✅ 移植（フォーム式 `POST`/`DELETE /api/Users/{id}/permissions`、#143 第4弾） |
| リソース/権限のツリーブラウズ選択 | `PermissionPicker` / `ResourcePicker`（建物→…→Point ツリー） | `ResourceTreePicker`（権限編集 + グループのリソース管理で共有） | ✅ 移植（遅延ロード再帰ツリー → 種別+ID に反映、#143 第7弾） |
| グループ一覧 | `groups/page.tsx` | `admin/groups/page.tsx` | ✅ 移植（読み取り、#143 第2弾） |
| グループ詳細（読み取り） | `groups/[id]/page.tsx` | `admin/groups/[id]/page.tsx` | ✅ 移植（属性 + リソースメンバー表示） |
| グループ作成 | `groups/new/page.tsx` | `admin/groups/new/page.tsx` | ✅ 移植（`POST /api/Groups`、#143 第3弾） |
| グループ更新 / 削除 | `groups/[id]/page.tsx` | `admin/groups/[id]/page.tsx` | ✅ 移植（`PUT` / `DELETE`、#143 第3弾） |
| グループのリソース管理 | `groups/[id]`（ResourcePicker） | `admin/groups/[id]/page.tsx` | ✅ 移植（フォーム式 `…/resources*` 追加/削除、#143 第5弾） |
| ハッシュ ID → 名前解決の表示 | あり | `admin/users/[id]/page.tsx` | ✅ 移植（`POST /api/Permissions/resolve`、ベストエフォート、#143 第6弾） |

## 認可（不変）

UI 出し分けは利便性に過ぎず、**認可は常に API 側で効く**（`UsersController` / `GroupsController` は
`IsAdmin` を全アクションで検査し、非管理者は 403）。移植によってこの境界は変わらない。

## 撤去（完了）

上表の **全 increment が web-client `(admin)` で完結**したため（#197〜#204）、`admin-console`
アプリをリポジトリ・CI・Helm・ArgoCD・OpenTofu・Keycloak realm から撤去した。users/groups 管理は
web-client の `(admin)` ワークスペース（`/admin` 配下）が唯一の正本。Traefik の `/admin` ルートは
撤去し、web-client の catch-all が `/admin` を配信する。Keycloak は `web-client` クライアントのみ
（旧 `admin-console` クライアントは削除）。

## 技術メモ（暫定）

- web-client 側の admin エンドポイント呼び出しは現状 **認証付き bespoke fetch**（`src/lib/admin/`）。
  `/api/Users` `/api/Groups` を OpenAPI/Swagger に掲載し、フロント型生成（openapi-typescript）へ
  置き換えるのを後続の整備課題とする（手書き型 `AdminUser` 等の解消）。
