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
| 2 | REST 互換 | `make openapi-breaking`（事前に `Tools/sync-type.bash`）。**前リリースのタグに対しても**実行する: `make openapi-breaking BASE=v<前版>` | 既定（`origin/main`）は breaking なし。前版に対する差分は、意図した変更だけで CHANGELOG に記載済み（rc.3 は `/api/v1` 移行の path 移動のみ。旧パスは書き換えで生きている） |
| 3 | full-stack E2E | `make demo-e2e` | PASS |
| 4 | レイク（保持 0） | `LAKE_RETENTION_DAYS=0` の compose で起動し、Parquet の Put / Get / query と compaction を確認。compaction は**終了した時間帯**だけが対象なので、`LAKE_COMPACTION_SETTLE_MINUTES=1 LAKE_COMPACTION_INTERVAL=1` を付け、時間境界（毎時 00 分）の後に `compact-*.parquet` を確認する（**`SETTLE_MINUTES=0` は無視されて既定の 30 分になる**） | `GET /api/v1/telemetries/query` が値を返し、バケットに lifecycle ルールが無い（`mc ilm rule ls`） |
| 5 | アップグレード smoke | 前版 → 候補 → ロールバック → 候補 を compose で実施（[§3.5](oss-upgrade-runbook.md#35-docker-compose-rc2--rc3-のドリル結果)）。前版の compose が使うイメージが取得できない場合は、前版のソースからアプリのイメージだけをビルドして入れ替える | 各段階で `/health`、web、一覧、テレメトリ、点制御（worker が `completed: Success`）が通る |

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
| 対象 SHA | `11bb4e3`（コードは同一、`780aa61` は argocd のイメージタグ更新のみ） | |
| 1 OSS Stack CI | **PASS**（6 ジョブ全て GREEN） | [run 37089562817](https://github.com/gutp-bim/gutp-building-os-ri/actions/runs/37089562817)。初回（`4dcd80f`）は E2E 2 ジョブが失敗し、#572 で修正 |
| 2 `make openapi-breaking` | **PASS**（既定）。rc.2 に対しては path 移動のみ | 既定: `No breaking changes.` / `v1.0.0-rc.2` に対して: 68 件すべて `api-path-removed-without-deprecation`（`/api/v1` への移動）。旧パス 8 本は 200 + `Deprecation` ヘッダで応答 |
| 3 `make demo-e2e` | **PASS** | CI の `UI E2E (demo full-stack)` が `make demo-e2e` そのもの（上の run）。ローカルでも 3 件 PASS |
| 4 レイク（保持 0） | **PASS** | `LAKE_RETENTION_DAYS=0`（既定）: バケットの lifecycle は `NoSuchLifecycleConfiguration`、part の書き込み・`telemetries/query`（range / latest）の読み出しが成功。compaction: 終了した時間帯 `hour=02` の **25 part（rc.2 と rc.3 の worker が書いたもの）が `compact-2026100302.parquet` にまとまり**（bldg-demo 43,091 行）、compaction 後の `query` は 30 行・重複なしで rc.2 時代の最古行（02:29:15）を含む |
| 5 アップグレード smoke | **PASS**（アプリ層） | [runbook §3.5](oss-upgrade-runbook.md#35-docker-compose-rc2--rc3-のドリル結果)。rc.2 → rc.3（API 先行 → web）→ rc.2 → rc.3 の 5 段階すべて OK、EF マイグレーション 4 → 8 件が自動適用、rc.2 のアプリは rc.3 のスキーマで動作 |

**発見したこと**: ゲート 1 を最初に実行した時点で E2E 2 ジョブが壊れていた（モックの置き去り、runner の uid、
Playwright イメージの版ずれ、建物の再選択でフロアが消える不具合、並行度テストの揺らぎ）。`oss-ci` は手動
トリガーのみで長く回っておらず、タグ直前に初めて実行したから見つかった。次のリリースでは**早めに 1 回
回しておく**こと。
