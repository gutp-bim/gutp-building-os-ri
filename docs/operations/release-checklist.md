# リリースチェックリスト

タグ（`v<VERSION>`）を打つ前に、**対象 SHA** に対して以下をすべて通し、結果をこのファイルの
「実施記録」にコピーして残します。CI は手動トリガーのみ（`oss-ci` は `workflow_dispatch`）なので、
**タグ直前の 1 回を実行して run URL を証跡にする**ことがリリースゲートです。

タグを打つと `harbor-push.yml` が全イメージ（api-server / connector-worker / gateway-bridge /
web-client）をビルドして push します。タグは取り消しにくいので、先にチェックを通します。

## 事前

- [ ] `VERSION` / `web-client/package.json` / `kubernetes/helm/*/Chart.yaml`（version と appVersion）が
      同じ版数
- [ ] `CHANGELOG.md` の `[Unreleased]` が空で、版数・日付つきのセクションに確定している。BREAKING が
      あれば「Upgrading from …」に移行手順がある
- [ ] 既知の制限（Known limitations）が CHANGELOG とリリースノートの両方にある
- [ ] open の `priority: critical` issue を確認し、残るものは Known limitations に記載した

## ゲート（対象 SHA で実施）

| # | 項目 | 手順 | 合格条件 |
|---|---|---|---|
| 1 | OSS Stack CI | Actions → *OSS Stack CI* → Run workflow（対象 SHA の `main`） | 全 job GREEN。run URL を記録 |
| 2 | REST 互換 | `make openapi-breaking`（事前に `Tools/sync-type.bash`） | breaking なし（意図した変更は CHANGELOG に記載済み） |
| 3 | full-stack E2E | `make demo-e2e` | PASS |
| 4 | レイク（保持 0） | `LAKE_RETENTION_DAYS=0` の compose で起動し、Parquet の Put / Get / query と compaction を確認 | `GET /api/v1/telemetries/query` が値を返し、バケットに lifecycle ルールが無い（`mc ilm rule ls`） |
| 5 | アップグレード smoke | 前版 → 候補 → ロールバック → 候補 を compose で実施（[手順](oss-upgrade-runbook.md)） | 各段階で `/health` と制御の end-to-end が通る。通ったら runbook の「未実施」注記を更新 |

## タグ後

- [ ] `harbor-push` が 4 イメージとも成功（GHCR に `v<VERSION>` タグ）
- [ ] GitHub Release を作成（RC は *pre-release*）。本文に CHANGELOG の該当セクションと Known limitations
- [ ] リリース準備トラッキング issue（#184）を更新

## GA（`1.0.0`）で追加で必要なもの

- [ ] `proto/` の `buf breaking` ゲート
- [ ] 12k Point 規模のヘルスイベント評価（PointHealthLedger / HealthEvaluator）の負荷試験結果
- [ ] アップグレード / ロールバックの実機ドリル（上記 5 を本番相当の規模で）
- [ ] RustFS の保持方針の確定（#492 / #489）
- [ ] legacy API の利用量（`building_os.api.legacy_requests`）の確認

## 実施記録

### v1.0.0-rc.3

| 項目 | 結果 | 証跡 |
|---|---|---|
| 対象 SHA | | |
| 1 OSS Stack CI | | |
| 2 `make openapi-breaking` | | |
| 3 `make demo-e2e` | | |
| 4 レイク（保持 0） | | |
| 5 アップグレード smoke | | |
