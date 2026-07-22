# エディション区分: Demo / Developer / Production（#163）

Building OS OSS を「誰が・どの目的で」使うかで **3 つのエディション**に整理する。構成要素が多く
初見では圧倒されがちなため（再評価 §3.1）、**同じコードベースを設定（compose profile / 認証 /
配信基盤）で段階的に立ち上げる**ものとして区分を明文化する。#155（demo）/ #161（認証）の決定と整合。

> これは**運用モードの区分**であり、別ビルドや別エディションのバイナリではありません。すべて同一の
> イメージ・同一のコードで、環境と設定だけが違います。

---

## 早見表

| 軸 | 🟢 Demo | 🔵 Developer（既定） | 🟠 Production |
|---|---|---|---|
| 目的 | 5分で「見る→制御する」を体験 | 本番相当構成で評価・開発 | 実運用 |
| 起動 | `make demo` | `docker compose -f docker-compose.oss.yaml up`（+ `--profile webclient`） | Kubernetes（Helm）+ ArgoCD + OpenTofu |
| 認証 | **摩擦ゼロ**（API は `DISABLE_AUTH=true`、Web はデモ自動ログイン, #161）| **Keycloak 統一**（API+Web とも JWT 必須, #161 案B）| Keycloak（外部 RDBMS 正本）+ mTLS ingress |
| データ | 既定シード twin（`GW-SOS-001`）+ sim フィーダで周期テレメトリ | 自分の twin を投入、実/シミュレータ GW を接続 | 実ゲートウェイ・実設備 |
| 制御 | in-process `simulated`（実 GW 不要で 200）| binding 次第（`hono`/`kandt`/`bacnet-sim`）| 実 GW（mTLS egress）|
| 可観測性 | なし | opt-in（`--profile observability`）| 常設（Prometheus/Grafana/Loki/Tempo）|
| テレメトリ層 | Parquet レイク（既定）| Parquet レイク（既定、`timescale` opt-in）| Parquet レイク（+ 保持/バックアップ運用）|
| 冗長・スケール | 単一プロセス | 単一ホスト | 水平スケール（GatewayBridge 等）+ 冗長 |
| 主なドキュメント | [getting-started](../guides/getting-started.md) / [#155 demo] | [getting-started](../guides/getting-started.md) / [system-architecture](../architecture/system-architecture.md) | [oss-production-deployment](oss-production-deployment.md) + 運用 Runbook 群 |

---

## 🟢 Demo — 「まず動くのを見たい」

- **1コマンド**: `make demo`（= `docker compose -f docker-compose.oss.yaml -f docker-compose.demo.yaml --profile demo --profile webclient up -d --build`）。
- 既定シード twin（`fixtures/e2e/twin.ttl` の `GW-SOS-001` / `SOS-PT-001..008`, #124）へ demo フィーダが
  gRPC GatewayIngress で周期テレメトリを投入。`/resources` にすぐデータが出る。
- 制御は GW-SOS-001 を in-process `simulated` にマップするので、実ゲートウェイ無しで 200 が返る。
- **認証は摩擦ゼロ**: デモオーバーレイが API を `DISABLE_AUTH=true` にし、Web Client はデモ自動ログイン
  （#161 のフォロー、認証フローをスキップ中であることは UI 上で明示）。**本番との乖離があるため、
  デモ専用**。
- 想定ユーザー: 初見の評価者、デモ提示。

## 🔵 Developer（既定スタック）— 「本番に近い状態で評価・開発したい」

- 起動: `docker compose -f docker-compose.oss.yaml up -d`（Web も見るなら `--profile webclient`）。
- **認証は Keycloak に統一（#161 案B, #226）**: API も Web も JWT 必須で、「curl は素通りなのに画面は
  ログイン必須」という非対称がない。dev realm（`oss-stack/keycloak/realm.json`）に `admin`/`admin`・
  `testoperator`/`testpass` が投入済み。curl は [getting-started](../guides/getting-started.md) のトークン取得手順。
- 全 API・全画面・コネクタ拡張・twin 編集・制御 binding を一通り触れる。可観測性は必要時のみ
  `--profile observability`、MQTT は `--profile mqtt`、レガシー warm は `WARM_STORE=timescale` +
  `--profile timescale`。
- `DISABLE_AUTH=true` は**開発者向けオプトイン**（必要時に自分で有効化）に格下げ。
- 想定ユーザー: 導入検討の技術者、コネクタ/アプリ開発者。

## 🟠 Production — 「実運用する」

- 配信: Kubernetes（`kubernetes/` Helm）+ ArgoCD（GitOps, `argocd/`）+ OpenTofu（`opentofu/`）。
- 認証/境界: Keycloak（**外部 RDBMS を正本**）、**mTLS ingress**（ゲートウェイの `X-Gateway-Id`
  trusted header 注入, #224 / [oss-gateway-security-ops](oss-gateway-security-ops.md)）。
- 可観測性を常設、GatewayBridge 等を水平スケール（per-gateway NATS ルーティング）。
- 運用: [本番デプロイ](oss-production-deployment.md) / [バックアップ・リストア](oss-backup-restore-runbook.md) /
  [アップグレード](oss-upgrade-runbook.md) / [障害対応](oss-incident-runbook.md) Runbook。
- 想定ユーザー: 運用者・SRE。
- **注意（再評価 §3.3）**: 大規模実運用性能は未実証（E1–E8 は単一ホスト・単一ビル・小規模）。
  多棟・大量 Point・長期保存・大量再接続の評価は #163 / #202 の残課題。現時点では「設計の妥当性を
  確認済み」と表現し「大規模実運用性能を実証済み」とは主張しない。

---

## 区分と設定の対応（要点）

| 設定 | Demo | Developer | Production |
|---|---|---|---|
| `DISABLE_AUTH` | `true`（デモ overlay）| `false`（既定, #161）| `false` |
| compose profile | `demo` + `webclient` | 既定（必要に応じ `observability`/`mqtt`/`timescale`）| N/A（Helm）|
| Web ログイン | デモ自動ログイン（#161）| Keycloak ログイン | Keycloak ログイン |
| 制御 binding | `GW-SOS-001` → `simulated` | 任意（`hono`/`kandt`/`bacnet-sim`）| 実 GW |
| 監視 | なし | opt-in | 常設 |

## 参照

- #155（demo プロファイル）/ #161（認証方針）/ #163（本番製品化トラッキング）
- [getting-started.md](../guides/getting-started.md) / [system-architecture.md](../architecture/system-architecture.md) /
  [oss-production-deployment.md](oss-production-deployment.md)
- Runbook: [backup-restore](oss-backup-restore-runbook.md) / [upgrade](oss-upgrade-runbook.md) /
  [incident](oss-incident-runbook.md)
