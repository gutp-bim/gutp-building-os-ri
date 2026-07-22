# Building OS ドキュメント

Building OS OSS の利用・開発・運用ドキュメントです。目的に合う入口から読み始めてください。

## 最初に読む

1. [基本概念](guides/concepts.md)
2. [Getting Started](guides/getting-started.md)
3. [システムアーキテクチャ](architecture/system-architecture.md)

ゲートウェイを接続する場合は [Gateway Integration](guides/gateway-integration.md)、本番導入は
[本番デプロイ構成](operations/oss-production-deployment.md) を続けて参照してください。

## ディレクトリ

| ディレクトリ | 内容 |
|---|---|
| [`guides/`](guides/) | 利用者・アプリ開発者・コネクタ開発者向けの手順 |
| [`architecture/`](architecture/) | 現行システムの構成、契約、設計 |
| [`operations/`](operations/) | デプロイ、監視、セキュリティ、移行、障害対応の Runbook |
| [`reference/`](reference/) | 評価結果、比較資料、レポートテンプレート |
| [`project/`](project/) | PRD、移行計画、ロードマップ、完了済みレビュー |
| [`adr/`](adr/) | Architecture Decision Records |
| [`agents/`](agents/) | コーディングエージェント向けのプロジェクト情報 |
| [`schema/`](schema/) | 生成された OpenAPI 定義 |

HTML、CSS、画像は GitHub Pages の公開サイト用資産です。

## よく使う文書

- [リソース管理](guides/resource-management.md)
- [Keycloak ユーザー管理](guides/keycloak-user-management.md)
- [API クライアント開発](guides/api-client-guide.md)
- [コネクタ開発](guides/connector-development-guide.md)
- [テレメトリ仕様](architecture/telemetry-specification.md)
- [Hot / Warm / Cold アーキテクチャ](architecture/oss-tier-architecture.md)
- [バックアップ・リストア](operations/oss-backup-restore-runbook.md)
- [障害対応](operations/oss-incident-runbook.md)
- [総合性能評価](reference/performance-evaluation-report.md)

## OpenAPI 定義

API エンドポイントの詳細は [`schema/swagger.yaml`](schema/swagger.yaml) を参照してください。

```bash
# API Server が起動している状態で実行
cd Tools
./generate_swagger.bash
```

> 本プロダクトは東京大学 グリーン ICT プロジェクトの研究成果物の派生物です。現状有姿で提供されます。
> 詳細は [ルート README の免責事項](../README.md#免責事項-disclaimer) を参照してください。
