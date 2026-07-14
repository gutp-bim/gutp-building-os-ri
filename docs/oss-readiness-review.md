# OSS 公開前レビュー（OSS Readiness Review）

- 実施日: 2026-07-04
- 対象: `gutp-bim/gutp-building-os-ri` `main`（`c74e982` 時点）
- 方法: 主要ファイル約 15〜20 点の精読 + リポジトリ全体の機械的走査（秘密情報・内部ホスト名・TODO 等）。
  実装変更は行っていない（本ドキュメントの追加のみ）。
- 制約: 読了ファイル上限 30。未読で確認を推奨するファイルは末尾「追加で読むべきファイル」に理由付きで列挙。

---

## 総評

OSS 公開の下地は**かなり良く整っている**。LICENSE（Apache-2.0）+ NOTICE + SECURITY.md + 免責事項、
`.env.example` による認証情報の外部化、squash 済みの公開履歴（過去コミットからの秘密漏えいリスクが小さい）、
ADR・網羅的な docs/、バックエンド 116 / フロントエンド 52 のテストファイル、コネクタ拡張手順の文書化など、
研究派生 OSS としては水準以上。

一方で、**即時に直すべき具体的な問題が 4 件**ある（ハードコードされた Basic 認証パスワード、
リリース用イメージビルドの context 不整合、CI 内の到達不能な deploy ジョブ、無条件の CORS 全開放）。
いずれも修正コストは小さい。

---

## すぐ直すべき（公開前 / 直後に必須）

### 1. ハードコードされた Basic 認証パスワード 【最優先】

`DotNet/BuildingOS.ApiServer/Middlewares/BasicAuthenticationMiddleware.cs:14-15` に
Swagger / ReDoc（`/swagger`, `/api-docs`）保護用の認証情報が平文で埋め込まれている
（`building-os` / `oP*4yzbN8jE7`）。`Startup.cs:388` で常時有効化され、
`BuildingOS.ApiServer/README.md` は「開発・本番ともに使用」と明記している。

- リポジトリが公開された時点でこのパスワードは公知。**本番相当環境で同じ値を使っている場合は即ローテーション**が必要。
- 修正方針: 環境変数（例 `SWAGGER_BASIC_AUTH_USER` / `SWAGGER_BASIC_AUTH_PASSWORD`）から読み、
  未設定時は Swagger 公開を無効化 or 認証を要求しない開発モードに倒す。コンストラクタは既に
  `IConfiguration` を受けているのに未使用なので変更は局所的。
- 比較検証もタイミングセーフでない（`==` 比較）が、まず値の外部化が先。

### 2. `harbor-push.yml` のビルド context 不整合（リリースパイプライン破損）

`.github/workflows/harbor-push.yml` は api-server / connector-worker を `context: DotNet` でビルドするが、
両 Dockerfile は**リポジトリルート context 前提**（`COPY ./proto/`, `COPY ./DotNet/`。
`docker-compose.oss.yaml:316-320` のコメントにも明記）。`DotNet/proto/` は存在しないため
`COPY ./proto/` が失敗し、main への push で走るこのワークフローは**イメージを作れない**はず。

- 修正: `context: .` に統一（web-client は `context: web-client` のままで整合するか Dockerfile を要確認）。
- これが通らないと下流の `argocd-image-update.yml`（`workflow_run` 連鎖）も機能しない。

### 3. `oss-ci.yml` の到達不能な deploy ジョブ

`oss-ci.yml` のトリガーは `workflow_dispatch` のみだが、`deploy` ジョブの条件は
`github.event_name == 'push'`（135-141 行）— **永遠に実行されない死にコード**。しかも
`secrets.KUBECONFIG` で "production" environment へ Helm デプロイする内容で、OSS リポジトリの
CI としては外部コントリビューターを混乱させる。

- 修正: deploy ジョブを削除するか、別ワークフロー（デプロイ運用者向け・明示的 dispatch）に分離。

### 4. CORS が無条件で全開放

`DotNet/BuildingOS.ApiServer/Startup/IServiceCollectionExtension.Cors.cs` の `AddCorsForAll()` が
`AllowAnyOrigin/AnyMethod/AnyHeader` を**環境を問わず**適用し、gRPC-web エンドポイントにも同じ
ポリシーが付く。Bearer トークン方式なので即座に致命ではないが、本番前提の設定手段が存在しないのは
プラットフォーム製品として不備。

- 修正: `CORS_ALLOWED_ORIGINS`（CSV）等の環境変数でオリジンを設定可能にし、未設定時のみ
  開発用全開放（または開発環境限定）にする。README の環境変数表にも追記。

### 5. 内部作業アーティファクトの除去

- `docs/repository-review.html` — 内部向け技術レビューの成果物（HTML 一枚もの）。docs の
  ナビゲーションからも参照されておらず、公開ドキュメント体系と重複・齟齬を生むので削除か
  `docs/` 外へ。
- `.claude/scheduled_tasks.lock` — AI エージェント作業の残骸。削除して `.claude/` を `.gitignore` に追加。

---

## 後回しでよい（公開後 1〜2 イテレーションで）

### 6. Docker イメージタグの `:latest` 固定不足（再現性）

`docker-compose.oss.yaml` で OxiGraph / MinIO / Keycloak / Prometheus / Grafana / Loki / Tempo /
postgres-exporter / pgbouncer / Ollama が `:latest`。新規ユーザーの初回起動が上流の破壊的変更で
壊れる典型パターン。NATS（`2.10-alpine`）や otel-collector（`0.104.0`）のようにメジャー・マイナーで
ピン留めを推奨。Helm の既定 `tag: latest`（`kubernetes/helm/building-os/values.yaml`）も同様。
ついでに compose 先頭の `version: "3.8"` は Compose v2 では obsolete 警告が出るので削除してよい。

### 7. フロントエンドのデフォルト API URL 不整合（オンボーディング摩擦）

`NEXT_PUBLIC_API_BASE_URL` 未設定時のフォールバックが揃っていない:

- `web-client/src/lib/infra/api-client/client.ts:54` ほか 5 箇所 → `http://localhost:8081`
- `web-client/src/lib/infra/grpc-client/index.ts:4` → `http://localhost:8080`
- 実際のローカル API Server（`WithLocal`）→ **`http://localhost:5000`**

README 手順どおりに `yarn dev` + `dotnet run` すると素の状態では API に繋がらない。
フォールバックを `5000` に統一し、`web-client/.env.example`（`NEXT_PUBLIC_API_BASE_URL` /
`NEXT_PUBLIC_KEYCLOAK_*` / `NEXT_PUBLIC_ASSISTANT_ENABLED`）を追加するのが早い。

### 8. ドキュメントのドリフト（新規ユーザーが最初に踏む）

- `AGENTS.md`: 「TimescaleDB for telemetry」— #216 以降の既定は Parquet レイク。要更新。
- `README.md` 環境変数表: `LOG_LEVEL` を現役として記載しているが、`CLAUDE.md` では
  **deprecated（no effect）** と明記。`Logging__LogLevel__*` に統一を。`launchSettings.json` や
  Helm values にも `LOG_LEVEL` が残存。
- `README.md` の ConnectorWorker 環境変数表が 3 変数のみで、実際の必須群
  （`OXIGRAPH_ENDPOINT`、`WARM_STORE`/`MINIO_*` など）と乖離。CLAUDE.md 側の詳細表へのリンクか転記を。
- ~~`oss-ci.yml` の frontend-check が Node `20.x`、README / CLAUDE.md の要求は Node 22+。統一を。~~
  → **解消済み (#164):** CI（`oss-ci.yml` / `pr-check.yml`）は Node `22.x`、`.nvmrc`/`engines` は
  `20.19.5`。README / CLAUDE.md / getting-started / CONTRIBUTING を「最低 20.19.5 / 推奨 22.x」に統一。

### 9. Helm チャートの Azure 残滓（ベンダーロック掃除の残り）

`kubernetes/helm/building-os/values.yaml:30-33` に `COSMOS_DATABASE_NAME` /
`COSMOS_CONNECTION_STRING` 等が残存。AGENTS.md が「Azure 依存を再導入しない」と宣言している以上、
既定 values からは落とすべき。また `secretEnv` に `KEYCLOAK_REALM`（非秘密）が入っているなど
env / secretEnv の仕分けも見直し対象。

### 10. 細かい品質項目

- `DotNet/BuildingOS.ApiServer/Properties/launchSettings.json` 末尾に trailing comma（厳密 JSON 違反。
  現状の SDK は許容するがツールによっては壊れる）。
- `Startup.cs:411` `/health` のレスポンスに `Version = "1.0.0"` がハードコード。アセンブリ版数か
  ビルドメタデータから取得を。
- `BuildingOS.DuckDbSpike` / `.Test` がメインソリューションに同居。スパイクなら `Tools/` 配下へ移すか
  README で experimental と明示（`LakeBackfill` は runbook があるので現状で可）。
- 残 TODO は 4 件のみで健全。ただし `point-control-modal.tsx` の「swagger + aspida 再生成後に削除」
  系 3 件は生成フローを回せば消せるので早めに。

---

## 公開後 Issue でよい（コミュニティ運営・改善）

### 11. OSS ガバナンスの定型ファイル

- `CODE_OF_CONDUCT.md` 不在（CONTRIBUTING.md は Covenant「の精神」に言及するのみ）。
- `.github/ISSUE_TEMPLATE/` / PR テンプレート不在。
- `CHANGELOG.md` / リリースタグ / バージョニング方針（SemVer?）不在。`harbor-push.yml` は
  `v*.*.*` タグに反応する設計なので、タグ運用を決めるだけで釣り合う。
- CODEOWNERS / メンテナ体制の明文化。

### 12. 外部 PR に対する自動チェックの不在

テスト系 CI を `workflow_dispatch` のみに絞る方針（クレジット節約）は文書化されており妥当だが、
**外部コントリビューターの PR には一切の自動チェックが走らない**。最低限の軽量ゲート
（lint / typecheck / unit test のみ、`pull_request` トリガー + paths filter）を用意するか、
「メンテナが手動で dispatch する」運用を CONTRIBUTING.md に明記することを推奨。
併せて Dependabot / Renovate、CodeQL、secret scanning の有効化も Issue 化。

### 13. README の英語版

README / getting-started / 主要 docs が日本語のみ。国際的な OSS 利用者向けに、少なくとも
README の英語サマリ（アーキテクチャ図 + クイックスタート）を用意する。

### 14. サイト固有の ArgoCD 参照環境の一般化

`argocd/values/*utokyo-eng2*.yaml` / `harbor.eng2.buildingos.local` は「reference environment」と
明記されておりホスト名も `.local` なので漏えいの問題はないが、`argocd-image-update.yml` が
この値ファイルを main に自動コミットする（`[skip ci]`）構造は OSS リポジトリと運用リポジトリの
密結合。将来的に環境 values を別リポジトリ（GitOps repo）へ分離するのが定石。

### 15. API 面の積み残し（既知の follow-up の Issue 化）

- 管理系エンドポイント（`/api/Users` 等）が Swagger / Aspida 生成外で bespoke fetch
  （CLAUDE.md にも follow-up と明記）。公開 Issue にして追跡可能に。
- `GET /health` が匿名で `Environment` 名を返す（軽微な情報開示）。運用者向けに注記か削減。

---

## 観点別サマリ

| 観点 | 評価 | 要点 |
|------|------|------|
| アーキテクチャ | ◎ | NATS を中核にした ingest→validate→lake の一方向フロー、point-id 正準の gateway 契約、Hot/Warm/Cold 階層が ADR + docs で一貫。責務分割（ApiServer / ConnectorWorker / GatewayBridge / Shared)も明快。 |
| テスト | ○ | ユニット 116 ファイル + FE 52、Testcontainers 統合テスト、golden / parity / E2E(k6) ハーネスと多層。弱点はテストが CI で自動実行されないこと（#12）。 |
| CI/CD | △ | 手動 dispatch 方針自体は文書化済みだが、到達不能 deploy ジョブ（#3）、harbor-push の context 破損（#2）、外部 PR 無チェック（#12）。 |
| README | ○ | 構成・ポート表・環境変数・docs 索引まで充実。ドリフト（#8）と FE デフォルト URL（#7）が新規ユーザーの初回体験を削る。英語版なし（#13）。 |
| ライセンス | ◎ | Apache-2.0 + NOTICE + 出自・免責の明記 + CONTRIBUTING のライセンス同意条項。体裁は十分。 |
| 設定ファイル | ○ | 認証情報は `${VAR:-default}` で外部化され dev-only と明記、`.env.example` あり。`:latest` 多用（#6）と Basic 認証ハードコード（#1）が例外。 |
| API 設計 | ○ | REST(Swagger 生成) + gRPC(proto 正準)、統合 `/telemetries/query`、ETag ベースの pointlist 同期など設計は良い。管理 API の生成外運用（#15）と CORS（#4）が残件。 |
| 将来の拡張性 | ◎ | コネクタ追加手順（Schema→生成→Worker→登録→テスト）が文書化され、gateway binding は registry ベースの config 駆動。help/onboarding も content-as-code。 |
| ベンダーロック回避 | ○ | Azure マネージド→OSS 置換が完了し AGENTS.md で再導入禁止を宣言。IoT Hub ハンドラは互換ブリッジとして隔離済み。Helm の COSMOS_* 残滓のみ（#9）。 |

---

## 秘密情報スキャン結果

- ハードコード実クレデンシャル: **1 件**（#1 の Basic 認証。要ローテーション）。
- `oss-stack/keycloak/realm.json` の `change-me-in-production` / `admin` / `testpass`、
  compose の `buildingos` / `buildingos123` 等はすべて **開発用と明記済み**（SECURITY.md /
  README / compose コメント）で、扱いとして適切。
- git 履歴は squash 済み（`initial public release` 起点）で、過去履歴からの漏えい面は小さい。
- 内部ホスト名は `*.eng2.buildingos.local`（参照環境、`.local`）のみで実害なし。

---

## 追加で読むべきファイル（本レビューの未読分・理由付き）

| ファイル | 理由 |
|---------|------|
| `DotNet/BuildingOS.ApiServer/Startup/IServiceCollectionExtension.Auth.cs` | JWT 検証パラメータ（issuer/audience/lifetime 検証の有効性）の確認。公開 API の認証強度の裏取り。 |
| `DotNet/BuildingOS.ApiServer/.../TestAuthenticationHandler.cs` | `DISABLE_AUTH=true` 時に付与される権限（admin 相当か）の確認。SECURITY.md のスコープ外宣言と実装の一致検証。 |
| `DotNet/BuildingOS.ApiServer/Controllers/GatewayProvisioningController.cs` | `X-Gateway-Id` 信頼ヘッダの検証実装が docs/oss-gateway-pointlist-sync.md の設計どおりかの確認（trust boundary）。 |
| `oss-stack/nats/nats-server.conf` | ローカルスタックの NATS に認証が無い場合、本番展開手引き（Helm/docs）側で auth/TLS がカバーされているかの突合。 |
| `kubernetes/helm/building-os/templates/`（secret/deployment） | `secretEnv` が Secret として正しくレンダリングされるか（ConfigMap に漏れていないか）。 |
| `web-client/middleware.ts` + `src/lib/admin/http.ts` | `oidc.access_token` Cookie の属性（httpOnly/secure）とトークン取り扱いの確認。 |
| `docs/oss-gateway-security-ops.md` | mTLS 運用手順が Traefik 設定例と整合しているか。 |
| `Tools/auth-proxy-server/` | 開発用認証バイパスツールの公開適性（誤って本番導線に載らない作りか）。 |
| `scripts/validate-oss-issue-readiness.sh` | CI の oss-readiness ジョブが何を保証しているかの把握（本レビューとの重複/補完関係）。 |
| `opentofu/environments/` | tfvars の例に実値が混ざっていないかの最終確認（`.gitignore` は `*.secrets.tfvars` を除外済み）。 |

---

## 推奨アクション順序（要約）

1. #1 Basic 認証の外部化 + 該当パスワードのローテーション（数十分）
2. #2 harbor-push の context 修正、#3 dead deploy ジョブ削除（数十分）
3. #4 CORS のオリジン設定化、#5 内部アーティファクト除去（半日）
4. #6〜#10 を次のメンテナンス PR でまとめて（1〜2 日）
5. #11〜#15 を GitHub Issue 化してラベル `good first issue` / `help wanted` の種にする
