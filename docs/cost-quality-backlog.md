# コスト最適化・品質改善バックログ

> OSS 公開前レビュー（`docs/oss-readiness-review.md`）の後続として、リポジトリ全体を
> コスト（CI クレジット / コンテナ / ストレージ / 可観測性 / ローカル開発機）と
> 品質（テスト / ビルド / 依存管理 / ドキュメント）の観点で棚卸ししたもの。
> **本書は抽出のみで実装は含まない。** 各項目は Issue 化して個別に進める前提。
>
> 優先度: ★★★ = 早期に着手すべき / ★★ = 次イテレーション / ★ = 余裕があれば。
> 工数: S = 半日以内 / M = 1〜2日 / L = 3日以上。

---

## A. コスト最適化

### A-1. OTel トレースが 100% サンプリング ★★★ / S

`OtelSetup.cs` にサンプラー設定がなく、既定の ParentBased(AlwaysOn) で**全リクエストのトレース**が
Tempo に送られる。テレメトリ流量が増えるほど otel-collector / Tempo のストレージ・CPU を線形に消費する。

- 改善案: `OTEL_TRACES_SAMPLER=parentbased_traceidratio` + `OTEL_TRACES_SAMPLER_ARG`（例 0.1）を
  環境変数で設定可能にし、Helm / compose の既定を 10% 程度に。ヘルスチェック系パス
  （`/health*`）はサンプリング前に除外（`AddAspNetCoreInstrumentation` の Filter）。
- 期待効果: Tempo ストレージ・collector CPU を最大 ~90% 削減。

### A-2. API Server Dockerfile のレイヤーキャッシュ破綻 ★★★ / S

`DotNet/BuildingOS.ApiServer/Dockerfile` はコメントで「csproj を先にコピーして restore を
レイヤー分離」と書きながら、実際は `COPY ./DotNet/ ./` で**ソース全体を restore 前にコピー**
している。1 行の変更でも restore レイヤーが無効化され、`dotnet restore` がフル実行される
（GHA キャッシュ済みでも数分 × 2 イメージ × main マージ毎）。ConnectorWorker 側も同型。

- 改善案: `COPY **/*.csproj` → `dotnet restore` → `COPY` 残り、の正攻法に修正。
  あわせて `.dockerignore` に `**/bin` `**/obj` `**/TestResults` があるか確認。
- 期待効果: main マージ毎のビルド時間を大幅短縮（Actions 分数 = クレジット削減）。

### A-3. `dotnet restore/publish` がソリューション全体を対象 ★★ / S

同 Dockerfile が `dotnet restore ./`（= テスト・`DuckDbSpike`・`LakeBackfill` 含む全プロジェクト）を
restore してから ApiServer だけ publish している。不要プロジェクトの NuGet 取得と
ビルドグラフ評価が毎回走る。

- 改善案: ソリューションフィルタ（`.slnf`）か `dotnet publish DotNet/BuildingOS.ApiServer -c Release`
  への一本化（publish は暗黙 restore で依存プロジェクトのみ辿る）。
- 期待効果: イメージビルド時間・SDK ステージのメモリ削減。

### A-4. harbor-push が変更有無に関わらず 3 イメージを毎回ビルド ★★ / M

`harbor-push.yml` は main への**全マージ**で api-server / connector-worker / web-client を
無条件に再ビルド・再プッシュする。ドキュメントだけの変更でも 3 ビルドが走る。

- 改善案: `dorny/paths-filter`（または `on.push.paths`）で `DotNet/**`・`proto/**` →
  バックエンド 2 件、`web-client/**` → web-client のみ、それ以外はスキップ。
  タグ push（`v*.*.*`）時は常に全ビルド。
- 期待効果: ドキュメント・IaC 変更時の Actions 消費をゼロに。

### A-5. ランタイムイメージの軽量化 ★★ / M

api-server / connector-worker は `mcr.microsoft.com/dotnet/aspnet:8.0`（Debian ベース, 約 220MB）を
そのまま使用。root 実行のまま（web-client は non-root 済み）。

- 改善案: `aspnet:8.0-noble-chiseled`（~110MB, 非 root, シェルなし）への切替を検証。
  ヘルスプローブが HTTP なので distroless 化の障害は少ない。切替不可なら `USER app` の追加のみでも可。
- 期待効果: レジストリ容量・pull 時間の半減 + 攻撃面の縮小（品質にも効く）。

### A-6. Parquet レイクの既定が「無制限保持」 ★★ / S

`LAKE_RETENTION_DAYS` 未設定 = 無期限、`PARQUET_STREAM_MAX_BYTES=0` = JetStream 無制限、
`PARQUET_QUERY_MAX_FILES=0` = クエリのオブジェクト数無制限。長期運用で MinIO / NATS の
ストレージが静かに膨張し、単発クエリが全パーティションを走査し得る。

- 改善案: compose / Helm の**参照値として**現実的な既定を明示
  （例: `LAKE_RETENTION_DAYS=365`, `PARQUET_STREAM_MAX_BYTES` に数 GB、
  `PARQUET_QUERY_MAX_FILES=5000`）。コード既定は変えず、デプロイテンプレート側で提示。
- 期待効果: 新規運用者がストレージ暴走を踏まない。クエリ課金（S3 API 呼び出し）の上限化。

### A-7. ローカル開発スタックの「lite プロファイル」 ★★ / M

`docker-compose.oss.yaml` は常時 Prometheus + Grafana + Loki + Tempo + otel-collector +
postgres-exporter + pgBouncer×2 を起動する。アプリ開発だけしたい開発者のマシンでも
可観測性 6 コンテナ分の CPU/メモリを消費。

- 改善案: 可観測性系を `--profile observability` に括り出し、既定は
  NATS / PostgreSQL / OxiGraph / MinIO / Keycloak + アプリのみの最小構成にする
  （`SYSTEM_STATUS_HEALTH_TARGETS` / `PROMETHEUS_URL` は未設定時 degrade 済みなので互換）。
- 期待効果: ローカル起動時間・メモリの大幅削減、新規コントリビューターの参入障壁低減。

### A-8. oss-ci の細かな無駄 ★ / S

- `dotnet tool install dotnet-ef` を毎回ネットワークインストール（キャッシュなし）。
- NuGet パッケージキャッシュ（`actions/setup-dotnet` の `cache: true`）未使用。
- 改善案: `cache: true` + `cache-dependency-path`、EF ツールはローカルツールマニフェスト
  （`dotnet tool restore`）に載せてキャッシュ対象にする。
- 期待効果: dispatch 1 回あたり数分短縮。

### A-9. Prometheus スクレイプ間隔・保持の見直し ★ / S

`scrape_interval: 15s` / 保持 15d は参照環境としてやや高頻度。OSS 既定は 30〜60s でも
KPI（`GET /api/system/status`）の用途には十分。

- 改善案: 既定 30s へ変更し、コメントで調整方法を案内。Tempo の `block_retention`
  （現状コメントアウト、既定値依存）も明示値（例 168h）を有効化。
- 期待効果: Prometheus TSDB / Tempo ブロックのディスク削減。

---

## B. 品質改善

### B-1. oss-ci が `Shared.Test` しか実行していない ★★★ / S

`oss-ci.yml` の "Unit Tests (Shared)" は `BuildingOS.Shared.Test` のみ。
**`BuildingOS.ApiServer.Test`（今回の PR で追加したテスト含む）、`GatewayBridge.Test` が
CI で一度も実行されない。** フロントエンドも vitest（テストファイル 52 件）があるのに
frontend-check は typecheck + lint のみで `yarn test` を回していない。

- 改善案: dotnet-test ステップを
  `dotnet test BuildingOS.sln --filter "FullyQualifiedName!~IntegrationTest&FullyQualifiedName!~DuckDbSpike"`
  に統一。frontend-check に `yarn test` を追加。
- 期待効果: 既存テスト資産が実際にゲートとして機能する。**最優先。**

### B-2. 外部 PR への軽量自動ゲート ★★★ / M

（レビュー #12 の再掲・具体化）テスト系 CI が `workflow_dispatch` のみのため、外部 PR には
何のチェックも走らない。クレジット節約方針と両立する最小構成を用意する。

- 改善案: `pull_request` トリガー + paths filter の軽量ワークフローを新設
  （lint / typecheck / 変更領域のユニットテストのみ、concurrency cancel-in-progress、
  fork PR は `pull_request` の read-only トークンで安全）。B-1 と同時に設計。
- 期待効果: 壊れた PR の早期検出、レビュアー負荷削減。

### B-3. 依存関係の自動更新とセキュリティスキャン ★★★ / S

`.github/dependabot.yml` なし、CodeQL なし、secret scanning の明示設定なし。
`coverlet.collector` が 6.0.0 / 3.1.2 と混在しているのは更新自動化がない兆候。

- 改善案: Dependabot（nuget / npm / github-actions / docker、weekly + grouped）導入、
  CodeQL（C# / TypeScript、schedule weekly）、リポジトリ設定で secret scanning + push protection 有効化。
- 期待効果: 依存の陳腐化・既知 CVE の検出を自動化。OSS としての信頼性向上。

### B-4. `Directory.Build.props` によるビルド設定の一元化 ★★ / S

各 csproj が `Nullable` 等を個別に持ち、`TreatWarningsAsErrors` は未設定。
共通設定のドリフト（coverlet バージョン差異など）が既に発生している。

- 改善案: `DotNet/Directory.Build.props` を新設し `Nullable` / `ImplicitUsings` /
  `LangVersion` / `TreatWarningsAsErrors`（まず `WarningsAsErrors=nullable` から段階導入）を集約。
  `Directory.Packages.props`（Central Package Management）でパッケージバージョンも統一。
- 期待効果: null 安全性の後退（今回レビューで検出した CS8604 系）をコンパイルエラーで阻止。

### B-5. カバレッジ計測とレポート ★★ / S

coverlet.collector は全テストプロジェクトに入っているが、CI で `--collect` されず
カバレッジは誰にも見えていない。

- 改善案: oss-ci で `--collect:"XPlat Code Coverage"` + ReportGenerator で
  Markdown サマリを job summary に出力。ゲート（閾値）は導入せず可視化から始める。
- 期待効果: テスト投資の効果測定、薄い領域（Controllers 等）の特定。

### B-6. 統合テスト・golden テストの定期実行 ★★ / S

`integration-tests` / `golden-tests` / `parity-harness` は手動 dispatch のみで、
実行されない期間が長いほど「気づかない破損」が蓄積する。

- 改善案: `schedule:`（週 1、深夜）を追加し、失敗時のみ Issue 自動起票
  （`actions/github-script` で 1 本）。クレジット消費は週 1 に限定。
- 期待効果: リグレッションの検出遅延を最大 1 週間に制限。

### B-7. Swagger / Aspida 型の drift 検知 ★★ / M

`sync-type.bash` の手動運用のため、API 変更後にフロント型の再生成を忘れると
実行時にしか気づけない。`point-control-modal.tsx` の TODO 3 件（「swagger + aspida
再生成後に削除」）が滞留しているのはその兆候。

- 改善案: CI で Swagger 生成 → `git diff --exit-code web-client/src/lib/infra/aspida-client`
  の drift チェックを追加（生成には API Server のビルドが必要なので B-1 のジョブに相乗り）。
  滞留 TODO は生成フローを一度回して解消。
- 期待効果: 型と実装の乖離を PR 時点で検出。

### B-8. 管理系 API の Swagger 統合 ★★ / L

（レビュー #15 の再掲）`/api/Users` / `/api/Groups` / `/api/Permissions` が Swagger 生成外で、
web-client は bespoke fetch（`src/lib/admin/http.ts`）を使用。型安全性と drift 検知（B-7）の
恩恵を受けられていない。

- 改善案: 管理系コントローラを Swagger 定義に含め、Aspida 型を生成、
  `src/lib/admin/` を生成クライアント上の薄い façade に置換。
- 期待効果: 管理 UI の型安全化、bespoke HTTP 層の保守コスト削減。

### B-9. EF マイグレーション drift チェックの実効化 ★ / S

oss-ci の `has-pending-model-changes` が `continue-on-error: true` で、drift があっても
緑になる。理由コメント（手書き initial snapshot）は妥当だが、恒久放置すると
`migrations add` 時に初めて壊れる。

- 改善案: snapshot を一度再生成して整合させた上で `continue-on-error` を外す。
- 期待効果: マイグレーション追加時の手戻り防止。

### B-10. OSS ガバナンス定型の整備 ★ / S〜M

（レビュー #11 の再掲・Issue 化待ち）`CODE_OF_CONDUCT.md`、`.github/ISSUE_TEMPLATE/`、
PR テンプレート、`CHANGELOG.md` + リリースタグ運用（`harbor-push` は `v*.*.*` 対応済み）、
CODEOWNERS、README 英語版。

- 改善案: release-please 等でタグ・CHANGELOG を自動化すると B 系全体と整合が良い。
- 期待効果: コミュニティ受け入れ体制の明示、リリースの再現性。

---

## 推奨着手順

| 順 | 項目 | 理由 |
|----|------|------|
| 1 | B-1（テスト実行範囲） | 既存資産を活かすだけ。工数 S で品質ゲートが即機能 |
| 2 | A-2（Docker レイヤーキャッシュ） | main マージ毎に効く恒常コスト。工数 S |
| 3 | A-1(OTel サンプリング) | 運用コストの最大要因になり得る。工数 S |
| 4 | B-3（Dependabot / CodeQL） | 設定ファイル追加のみ。OSS 信頼性に直結 |
| 5 | B-2（外部 PR ゲート） | B-1 の成果物を流用して設計 |
| 6 | A-4 / A-7 / B-4 以降 | 上記が安定してから順次 |
