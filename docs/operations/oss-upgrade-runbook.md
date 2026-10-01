# アップグレード Runbook（#163）

Building OS OSS を**バージョン間でアップグレード**する手順。スキーマ（EF Core / Parquet レイク /
proto）互換の考え方、無停止アップグレードの順序、ロールバックを1ページに集約する。

> ⚠️ **この Runbook は実装（マイグレーション適用箇所・ストリーム/ack 契約・ArgoCD 配信）に基づいて
> 記述していますが、実バージョン跨ぎのアップグレードを実機で通した検証は未実施です。本番採用前に
> ステージングで一度ドライラン（旧→新→ロールバック）してください。**

関連: [oss-backup-restore-runbook.md](oss-backup-restore-runbook.md)（前段のバックアップ）,
[oss-production-deployment.md](oss-production-deployment.md), [argocd-gitops-guide.md](argocd-gitops-guide.md),
[oss-tier-architecture.md](../architecture/oss-tier-architecture.md)。

---

## 0. 大原則

1. **アップグレード前に必ずバックアップ**（[oss-backup-restore-runbook.md](oss-backup-restore-runbook.md)）。
   特に PostgreSQL は EF Core マイグレーションで**前進的に変更**されるため、切り戻しにはダンプが要る。
2. **スキーマ変更は expand → migrate → contract の2段階**で入れる（下記 §2）。1リリースで「追加」、
   数リリース後に「削除」。これで新旧アプリが同一 DB を共有する無停止ロールアウトが安全になる。
3. **データストアは後方互換を保つ**：Parquet レイクは immutable append（既存オブジェクトは書き換え
   ない）、proto は additive-only（フィールド番号の再利用・削除をしない）。

---

## 1. マイグレーションの適用点

- **PostgreSQL / EF Core**: API Server 起動時に `dbContext.Database.Migrate()` を実行
  （`DotNet/BuildingOS.ApiServer/Startup/Startup.cs`）。**新イメージの API が起動した時点で保留
  マイグレーションが自動適用**される。マイグレーション定義は
  `DotNet/BuildingOS.Shared/Migrations/`。
  - 適用は API のセッションプール経由ではなく `POSTGRES_MIGRATION_CONNECTION_STRING`
    （`building-os.pgbouncer-session`、セッションプール）を使う。
- **Parquet レイク**: スキーマ移行の仕組みは持たない。**append-only**（`part-*.parquet` /
  `compact-*.parquet` は決定的命名で上書き、既存は不変）。列を増やす変更は「読み手が旧オブジェクトの
  欠損列を許容できる」形（nullable 追加）に限る。破壊的なレイクスキーマ変更が必要なら別途バック
  フィル（[oss-lake-backfill-runbook.md](oss-lake-backfill-runbook.md) と同型の CLI 移行）。
- **proto（gRPC / NATS 契約）**: `.csproj` がビルド時にコンパイル。互換は**運用規約**で担保
  （フィールド番号を再利用/削除しない、必須化しない）。破壊的変更検出ゲート（`buf breaking`）は
  nexus-gateway 側に先行例があり、BOS へ導入するのは #163 のフォロー項目。

---

## 2. スキーマ変更の expand-contract（無停止の要）

破壊的に見える変更も2段階に割れば無停止で入る:

| 変更 | Expand（今回のリリース） | Contract（数リリース後） |
|---|---|---|
| 列追加 | nullable で追加。新コードのみ書き込む | （不要） |
| 列削除 | まず新コードが**読まなくする** | 実際に drop するマイグレーション |
| 列リネーム | 新列を追加し二重書き込み | 旧列を drop |
| NOT NULL 化 | まず全行を埋めるバックフィル + デフォルト | 制約を付与 |

原則: **ある1マイグレーションは、その時点で動いている旧アプリを壊してはならない**（ロールアウト中は
新旧が混在するため）。

---

## 3. アップグレード手順（GitOps / Kubernetes）

配信は ArgoCD。イメージタグは `argocd/values/<env>.yaml` に短 SHA で書かれ、Git にコミットバックすると
Argo が検知して同期します（`.github/workflows/argocd-image-update.yml`、`docs/operations/argocd-gitops-guide.md`）。

1. **バックアップ**（§0-1）。
2. リリースノート/マイグレーション差分を確認（`DotNet/BuildingOS.Shared/Migrations/` の新規、proto 差分、
   レイクスキーマ差分）。expand-contract 規約（§2）に反していないか。
3. 新イメージをレジストリへ push（`harbor-push` → `argocd-image-update` が values を更新）。
4. Argo 同期。**API Server の起動で EF マイグレーションが自動適用**される。
   - ローリング更新中は新旧 Pod が同一 DB を共有 → §2 の expand 段階なら安全。
5. `GET /health`（API）/ `GET /health/ready`（connector-worker、NATS 接続）で readiness を確認。
6. 検証（§5）。

### REST API の版とデプロイ順（ADR-0008、#507）

web-client は **`/api/v1` だけ**を呼ぶ。`/api/v1` を持たない API Server（#507 より前）と組み合わさると、UI の
API 呼び出しがすべて 404 になる。したがって:

- **API Server を先に**更新し、ready になってから web-client を更新する。`argocd-image-update` は両方のタグを
  同じコミットで更新するため、同期直後のロールアウト中だけ新 web-client が旧 API Server に当たる時間が
  生じうる（数分。ADR-0008 で許容と判断）。停止を避けたい環境では、web-client の Application の同期を
  一時的に止め（Argo CD の手動同期）、API Server が ready になってから同期する。
- 旧パス（`/buildings`、`/api/Groups` など）は API Server 側で `/api/v1` に書き換えられるので、外部
  アプリ・スクリプトは更新しなくても動く（応答に `Deprecation` が付く）。旧パスの利用状況は
  `building_os.api.legacy_requests{root}` で確認する。

### テレメトリの建物キーがトポロジー由来になる（#527）

ConnectorWorker の gRPC ingress は、テレメトリの `building`（Parquet レイクのパーティションキー
`building_id=…`）を、Point の `sbco:building` リテラルではなく **トポロジー（`hasPart` / `locatedIn` /
`hasPoint`）で到達した Building の `sbco:id`** から決めるようになった。トポロジーで建物に届かない Point
だけが従来どおりリテラルを使う。

- リテラルとトポロジーが食い違う Point（古い値が残っている等）は、更新後のテレメトリから**別の
  `building_id=` パーティション**に入る。食い違う Point の件数とサンプルは、ConnectorWorker が
  メタデータを読み込むたびに `placed point(s) whose sbco:building literal names a different building`
  の警告ログで出す。更新前にこのログ（または twin の `sbco:building` と所属建物の比較）で件数を確認する。
- **読み取り側（API Server）。** レイクの読み取りは「Point の建物」を学習して走査する建物を絞る（#273）。
  建物が切り替わった Point について、次のように動く。
  - 2 つの建物にまたがる行を一度でも見た Point は、プロセスが動いている間、二度と絞り込まない。
  - 絞り込んだ読み取りが何も見つけなかった場合は、全建物でもう一度読む。そこで別の建物の行を見つけた
    Point は、上と同じく以後絞り込まない。
  - 最新値の取得（latest）は、絞り込んだ建物で見つけた行より新しい時間帯に、他の建物の行がないかを
    確かめる。現在の時間帯に値がある Point は、追加の確認なしで返す。
  - **残る制約：** 片方の建物だけを学習してから 30 分（学習キャッシュの TTL）以内に、切り替えを**またぐ**
    期間を読むと、学習していない側の行が欠ける。その Point が以後絞り込まれなくなるのは、全建物を読む
    読み取りが両方の建物を見た後になる。食い違う Point があるときは、ConnectorWorker を更新した後に
    API Server を再起動して学習キャッシュを空にし、その Point を切り替えをまたぐ期間で一度読んでおくと、
    以後は絞り込まれない。
- compaction / ロールアップ / バックフィルの単位は建物ごとなので、食い違う Point の切り替え前後の
  データは別々の建物単位で処理される（データは失われない）。建物単位の保持やバックフィルを運用している
  場合は、切り替え前の期間を旧建物 ID で扱うこと。

### compose（単一ホスト）での等価

```bash
# 1) バックアップ  2) 新イメージ pull  3) 再作成（API 起動でマイグレーション適用）
docker compose -f docker-compose.oss.yaml pull
docker compose -f docker-compose.oss.yaml up -d
docker compose -f docker-compose.oss.yaml logs -f building-os.api   # マイグレーション適用ログを確認
```

---

## 4. ロールバック

- **REST API の版（ADR-0008）**: ロールバックは**デプロイと逆順**。web-client を先に戻し、その後で
  API Server を戻す。**API Server だけを #507 より前へ戻さない**こと（新 web-client の呼び出しがすべて
  404 になる）。
- **アプリ（コード）**: ArgoCD は Git が正本。`argocd/values/<env>.yaml` のイメージタグを**前のタグへ
  revert してコミット**すれば Argo が旧バージョンへ同期する。compose なら旧タグで `up -d`。
- **DB マイグレーション**: EF の前進的変更は**自動では戻らない**。
  - expand 段階の変更（nullable 追加等）は旧コードでも無害なので、**アプリだけ戻せばよい**（DB は前進の
    まま放置して安全）。これが expand-contract を守る最大の理由。
  - contract（drop 等）まで進めた後に戻す必要が出たら、**バックアップからのリストア**
    （[oss-backup-restore-runbook.md](oss-backup-restore-runbook.md) §3）が唯一確実。だから contract は
    「新バージョンが十分安定してから」入れる。
- **Parquet レイク / proto**: append-only / additive-only を守っていれば、旧バージョンは新オブジェクト・
  新フィールドを無視して動き続けられる（ロールバック不要）。

---

## 5. 検証チェックリスト

- [ ] API 起動ログに保留マイグレーションの適用完了が出て、例外がない。
- [ ] `GET /health`（API 200）/ `GET /health/ready`（connector-worker 200 = NATS Open）。
- [ ] 主要フロー: `/resources` 表示、`GET /api/v1/telemetries/query?...&latest=true`、制御 1 件が成功。
- [ ] web-client のブラウザ開発者ツールで、API 呼び出しが `/api/v1/…` へ飛び 404 が出ていない。
- [ ] ローリング更新中にエラー率・レイテンシが跳ねていない（Prometheus/Grafana を使う場合）。
- [ ] `point_control_audit` に新規行が記録される（制御の end-to-end 生存確認）。

---

## 6. 既知の制約 / 未検証

- 実バージョン跨ぎのアップグレード・ロールバックを実機で通した検証は未実施（本ドキュメント作成環境に
  Docker デーモンなし）。手順はマイグレーション適用点（`Startup.cs`）・ストリーム契約・ArgoCD 配信
  （`argocd/values`）に基づく。**本番採用前にステージングでドライラン**してください。
- proto の破壊的変更検出（`buf breaking`）ゲートは BOS 未導入（nexus-gateway に先行例）。導入までは
  proto 互換はレビューで担保。#163 のフォロー項目。
- 大規模データでのマイグレーション所要時間（長時間ロック等）は本 Runbook の対象外（大規模評価 #163）。
