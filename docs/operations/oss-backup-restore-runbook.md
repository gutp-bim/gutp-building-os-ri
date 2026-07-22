# バックアップ・リストア Runbook（#163）

Building OS OSS の**状態を持つストア**をバックアップ・リストアする手順。v1.0.0 の運用準備
（#163 / #184）として、3+1 ストアそれぞれの取得・復元・整合性・定期化を1ページに集約する。

> ⚠️ **この Runbook のコマンドは compose のサービス名・認証情報・バケット名・ポートに基づいて
> 記述していますが、Docker デーモンのある環境での end-to-end 実行検証はまだ行っていません
> （grounded but not yet exercised）。本番で依存する前に、必ずステージング環境で一度
> ドライラン（取得 → 別環境へ復元 → 起動確認）してください。** 特に OxiGraph `/store` の全件
> ダンプ挙動と Keycloak エクスポートのフラグは実機で確認することを推奨します。

関連: [oss-production-deployment.md](oss-production-deployment.md)（本番トポロジ）,
[oss-tier-architecture.md](../architecture/oss-tier-architecture.md)（Hot/Warm/Cold）,
[oss-lake-backfill-runbook.md](oss-lake-backfill-runbook.md)（レイク移行）,
[system-architecture.md](../architecture/system-architecture.md)。

---

## 1. 対象ストアと分類

Building OS の永続状態は 3 つの主ストア + Keycloak に分かれます。**「正本（authoritative）」**
（失うと復元不能）と**「再構築可能（rebuildable）」**（他から作り直せるキャッシュ/派生）を
区別することがバックアップ設計の要です。

| ストア | サービス / ボリューム | 保持データ | 区分 | バックアップ |
|---|---|---|---|---|
| **PostgreSQL 16** | `building-os.postgres` / `pg_data` | ユーザー/グループ/認可、`point_control_audit`（制御監査）、`system_config`（アプリ設定 #148） | **正本** | 必須 |
| **MinIO（Parquet レイク）** | `building-os.minio` / `minio_data`、バケット **`cold`** | Warm+Cold テレメトリ（`building_id=.../hour=...` パーティション、immutable append + compaction） | **正本**（実測データ） | 必須 |
| **OxiGraph（ツイン）** | `building-os.oxigraph` / `oxigraph_data` | 建物→フロア→空間→機器→ポイント階層、共有ポイントリスト（`gateway_id` 所有権） | **正本**（UI 編集分）／ 種 TTL があれば一部再構築可 | 必須 |
| Keycloak | `building-os.keycloak` / `keycloak_data` | realm・クライアント・ユーザー | dev は種 `realm.json` から再構築可／本番は外部 DB が正本 | 環境依存（§6） |
| NATS KV `telemetry-latest`（Hot） | `building-os.nats` / `nats_data` | 各ポイントの**最新値キャッシュ**のみ | **再構築可**（ライブ ingest とレイク read から再充填） | 不要 |

> **Hot（`telemetry-latest`）はバックアップ不要**：最新値の読み取りキャッシュであり、Warm/Cold は
> レイクが正本です（[oss-tier-architecture.md](../architecture/oss-tier-architecture.md)）。復元後はライブ ingest で
> 自然に再充填され、range 読み取りはレイクから返ります。
>
> **レイクの ILM 保持（`LAKE_RETENTION_DAYS`）はバックアップではありません**：これはデータの
> ライフサイクル（N 日で**削除**）であり、バックアップ（**保全**）とは目的が逆です。保持で消えた
> データはバックアップからしか戻せません。

---

## 2. 取得前の準備（整合性）

厳密な**ポイントインタイム整合**が必要なら、取得中に書き込みを止めます（推奨: 夜間バッチ or
メンテナンス枠）:

```bash
# ingest と書き込みを一時停止（読み取り UI は落とさなくても可）
docker compose -f docker-compose.oss.yaml stop building-os.connector-worker building-os.api
# … §3〜§5 のバックアップを取得 …
docker compose -f docker-compose.oss.yaml start building-os.connector-worker building-os.api
```

無停止で取る場合でも、各ストアは概ね独立に復元できます（レイクは immutable append、監査行は
独立、ツインは低頻度更新）。無停止バックアップは「取得時刻付近の数分が欠ける可能性」を許容する
運用としてください。

以下の例は取得物を `./backups/<store>/` に置く前提です（`mkdir -p backups/{postgres,minio,oxigraph,keycloak}`）。
タイムスタンプは UTC の `date -u +%Y%m%dT%H%M%SZ` を使います。

---

## 3. PostgreSQL（ユーザー/認可・監査・設定）

**pgbouncer を経由せず** `building-os.postgres` に直接ダンプします（pgbouncer は
トランザクションプールで `pg_dump` のセッション前提と相性が悪いため）。

### バックアップ（カスタム形式 = 選択リストア可・圧縮済み）

```bash
docker compose -f docker-compose.oss.yaml exec -T building-os.postgres \
  pg_dump -U "${POSTGRES_USER:-buildingos}" -d "${POSTGRES_DB:-buildingos}" -Fc \
  > "backups/postgres/buildingos-$(date -u +%Y%m%dT%H%M%SZ).dump"
```

### リストア

スキーマは EF Core マイグレーションで作られます（API 起動時適用）。**同一スキーマ版のダンプ**を
戻すのが原則です（アップグレードを跨ぐ復元はマイグレーション整合に注意）。

```bash
# 既存オブジェクトを置換しながら復元（空 DB でも既存 DB でも可）
docker compose -f docker-compose.oss.yaml exec -T building-os.postgres \
  pg_restore -U "${POSTGRES_USER:-buildingos}" -d "${POSTGRES_DB:-buildingos}" \
  --clean --if-exists --no-owner \
  < "backups/postgres/buildingos-<TS>.dump"
```

> `--clean --if-exists` で既存テーブルを drop してから再作成します。完全にまっさらな DB へ戻す
> なら、先に `dropdb`/`createdb` してから `pg_restore` でも可。

---

## 4. MinIO — Parquet レイク（`cold` バケット）

テレメトリの正本。バケット名は **`cold`**（`IParquetLakeWriter.LakeBucket` /
`ParquetLakeScan.Bucket`）。オブジェクトは決定的命名の immutable append なので、`mc mirror` の
**差分同期**が安価です。

ホストから（MinIO は `9000` を publish 済み）:

```bash
# mc のエイリアスを一度だけ設定（mc = MinIO Client）
mc alias set bos-oss http://localhost:9000 \
  "${MINIO_ROOT_USER:-buildingos}" "${MINIO_ROOT_PASSWORD:-buildingos123}"
```

### バックアップ（差分ミラー）

```bash
mc mirror --overwrite bos-oss/cold ./backups/minio/cold
```

### リストア

```bash
mc mirror --overwrite ./backups/minio/cold bos-oss/cold
```

> `telemetry-latest`（Hot KV）は復元不要です（§1）。復元直後の最新値が空でも、ライブ ingest と
> `PARQUET_LATEST_LOOKBACK_HOURS`（既定 24h）のレイク fallback で最新値は返ります。

---

## 5. OxiGraph — デジタルツイン

distroless イメージ（シェル/curl なし）なので **ホストから HTTP** で扱うか、**ボリュームを
スナップショット**します。

### 5a. オンライン: SPARQL Graph Store から全件ダンプ（推奨）

```bash
# 全 quad を N-Quads で取得
curl -s -H 'Accept: application/n-quads' http://localhost:7878/store \
  > "backups/oxigraph/twin-$(date -u +%Y%m%dT%H%M%SZ).nq"
```

リストア（クリーンに置換 = 先に全削除してから読み込み）:

```bash
# 1) 既存を全消去
curl -s -X POST -H 'Content-Type: application/sparql-update' \
  --data 'DROP ALL' http://localhost:7878/update
# 2) ダンプを読み込み
curl -s -X POST -H 'Content-Type: application/n-quads' \
  --data-binary "@backups/oxigraph/twin-<TS>.nq" http://localhost:7878/store
```

### 5b. オフライン: ボリュームスナップショット（堅牢）

```bash
# ボリューム名は compose プロジェクト名（=チェックアウトのディレクトリ名）が接頭辞
docker volume ls | grep oxigraph_data          # 実際の名前を確認
docker compose -f docker-compose.oss.yaml stop building-os.oxigraph
docker run --rm \
  -v <PROJECT>_oxigraph_data:/data -v "$PWD/backups/oxigraph:/backup" alpine \
  tar czf "/backup/oxigraph_data-$(date -u +%Y%m%dT%H%M%SZ).tar.gz" -C /data .
docker compose -f docker-compose.oss.yaml start building-os.oxigraph
```

### 5c. 種 TTL からの再構築（部分）

`OXIGRAPH_SEED_TTL_PATH`（既定 `/fixtures/e2e/twin.ttl`, #124）で起動時に既定グラフを**全置換**
します。ツインを UI（`/admin/twin`）で編集していない環境なら、**種 TTL を保全して再起動するだけ**で
復元できます。UI 編集分は種 TTL に含まれないため、その分は 5a/5b が必要です。

---

## 6. Keycloak（環境依存）

- **dev / OSS 既定**: realm は `oss-stack/keycloak/realm.json` から種投入されます。UI で作った
  ユーザー等は `keycloak_data` ボリュームにあります。§5b と同じ要領で `keycloak_data` を
  スナップショットするか、`kc.sh export` で realm＋users をエクスポートしてください。
- **本番**: Keycloak は**外部 RDBMS** を正本にすべきで、その DB のバックアップ（§3 と同じ流儀、
  あるいはマネージド RDB のスナップショット）が Keycloak のバックアップになります。ポッドの
  ローカルボリュームは正本にしないでください。

---

## 7. リストアの順序と起動

1. **アプリ停止**（api / connector-worker / web）。
2. ストアを復元: PostgreSQL（§3）・MinIO `cold`（§4）・OxiGraph（§5）は互いに独立に復元可。
3. Keycloak を復元（§6、必要な場合）。
4. **アプリ起動**（api → connector-worker → web）。API 起動で EF マイグレーションが整合を確認、
   Hot KV はライブ ingest で再充填。
5. 検証（§9）。

---

## 8. 定期化の指針

- **世代管理**: 例）日次 7 世代 + 週次 4 世代。取得物は**別ホスト/別リージョン**に退避（同じ
  MinIO/同じディスクに置くとディスク障害で共倒れ）。
- **cron / K8s CronJob** の骨子（OSS/compose 例）:
  ```bash
  # 毎日 02:15 UTC
  15 2 * * *  cd /opt/building-os && \
    docker compose -f docker-compose.oss.yaml exec -T building-os.postgres \
      pg_dump -U buildingos -d buildingos -Fc > backups/postgres/buildingos-$(date -u +\%Y\%m\%dT\%H\%M\%SZ).dump && \
    mc mirror --overwrite bos-oss/cold /mnt/offsite/cold && \
    curl -s -H 'Accept: application/n-quads' http://localhost:7878/store > backups/oxigraph/twin-$(date -u +\%Y\%m\%dT\%H\%M\%SZ).nq
  ```
- **保全 ≠ 保持**: `LAKE_RETENTION_DAYS` は削除ポリシー。バックアップの世代管理とは別に設定します。

---

## 9. 本番 / Kubernetes での等価手順

| ストア | 本番での推奨 |
|---|---|
| PostgreSQL | マネージド RDB のスナップショット + WAL アーカイブ（PITR）、または pgBackRest。§3 の論理ダンプは移行/検証用。 |
| MinIO / S3 | **バケットのバージョニング + クロスリージョンレプリケーション**（誤削除に強い）。加えて `mc mirror` の定期退避。 |
| OxiGraph | PVC の CSI ボリュームスナップショット + §5a の SPARQL ダンプをオブジェクトストレージへ定期退避。 |
| Keycloak | 外部 RDBMS のバックアップ（ポッドのローカルボリュームは正本にしない）。 |

配置・ネットワーク境界の詳細は [oss-production-deployment.md](oss-production-deployment.md)。

---

## 10. 検証チェックリスト

- [ ] `pg_restore` 後、`point_control_audit` と `system_config` の行数が取得時と一致する。
- [ ] MinIO `cold` のオブジェクト数/合計サイズが取得元と一致（`mc ls --recursive --summarize bos-oss/cold`）。
- [ ] OxiGraph 復元後、`/resources` にツイン階層が表示され、`GET /gateways/{id}/pointlist` が期待の
      ポイント数を返す。
- [ ] API 起動ログでマイグレーションがクリーンに完了（保留マイグレーションなし）。
- [ ] ライブ ingest 再開後、対象ポイントの最新値（Hot）が更新される。

---

## 11. 既知の制約 / 未検証事項

- 本 Runbook は Docker デーモンのある環境での **end-to-end 実行検証を未実施**（本ドキュメント作成
  環境に daemon がないため）。コマンドは compose のサービス名・認証情報・バケット（`cold`）・
  ポート（5432/9000/7878）・ボリューム名に基づいて記述しています。**本番採用前にステージングで
  一度ドライランしてください。**
- 特に検証推奨: OxiGraph 0.5 の `/store` 全件 GET/POST の挙動（バージョンにより GSP の
  デフォルトグラフ扱いが異なりうる）、Keycloak 26 の `kc.sh export` フラグ、compose プロジェクト名に
  依存する `<PROJECT>_oxigraph_data` ボリューム名。
- 障害対応 Runbook（NATS 断 / MinIO 障害 / Keycloak 障害 / gateway 大量切断）とアップグレード手順は
  #163 の別項目として残します（本 Runbook はバックアップ・リストアに限定）。
