# アップグレード Runbook（#163）

Building OS OSS を**バージョン間でアップグレード**する手順。スキーマ（EF Core / Parquet レイク /
proto）互換の考え方、無停止アップグレードの順序、ロールバックを1ページに集約する。

> ⚠️ **この Runbook は実装（マイグレーション適用箇所・ストリーム/ack 契約・ArgoCD 配信）に基づいて
> 記述していますが、実バージョン跨ぎのアップグレードを実機で通した検証は未実施です。本番採用前に
> ステージングで一度ドライラン（旧→新→ロールバック）してください。**

関連: [oss-backup-restore-runbook.md](oss-backup-restore-runbook.md)（前段のバックアップ）,
[oss-production-deployment.md](oss-production-deployment.md), [argocd-gitops-guide.md](argocd-gitops-guide.md),
[oss-tier-architecture.md](oss-tier-architecture.md)。

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
Argo が検知して同期します（`.github/workflows/argocd-image-update.yml`、`docs/argocd-gitops-guide.md`）。

1. **バックアップ**（§0-1）。
2. リリースノート/マイグレーション差分を確認（`DotNet/BuildingOS.Shared/Migrations/` の新規、proto 差分、
   レイクスキーマ差分）。expand-contract 規約（§2）に反していないか。
3. 新イメージをレジストリへ push（`harbor-push` → `argocd-image-update` が values を更新）。
4. Argo 同期。**API Server の起動で EF マイグレーションが自動適用**される。
   - ローリング更新中は新旧 Pod が同一 DB を共有 → §2 の expand 段階なら安全。
5. `GET /health`（API）/ `GET /health/ready`（connector-worker、NATS 接続）で readiness を確認。
6. 検証（§5）。

### compose（単一ホスト）での等価

```bash
# 1) バックアップ  2) 新イメージ pull  3) 再作成（API 起動でマイグレーション適用）
docker compose -f docker-compose.oss.yaml pull
docker compose -f docker-compose.oss.yaml up -d
docker compose -f docker-compose.oss.yaml logs -f building-os.api   # マイグレーション適用ログを確認
```

---

## 4. ロールバック

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
- [ ] 主要フロー: `/resources` 表示、`GET /telemetries/query?...&latest=true`、制御 1 件が成功。
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
