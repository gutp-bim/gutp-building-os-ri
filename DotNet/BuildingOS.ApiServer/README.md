# BuildingOS.ApiServer
[![AppService](https://img.shields.io/badge/Azure-AppService-orange?logo=azureFunctions&logoColor=white&style=flat-square)]()
[![AspDotnetCore](https://img.shields.io/badge/Asp.NetCore-Reference-007d9c?logo=azureFunctions&logoColor=white&style=flat-square)](https://learn.microsoft.com/ja-jp/aspnet/core/introduction-to-aspnet-core?view=aspnetcore-8.0)

CosmosDB・Digital Twins からデータを取得し REST API として配信する ASP.NET Core アプリケーション

## Directory Structure

```
.
├── Controllers/                # API エンドポイント
│   ├── BuildingController.cs       # ビル情報 (/buildings)
│   ├── DeviceController.cs         # デバイス情報 (/devices)
│   ├── DeviceDetailController.cs   # デバイス詳細 (/device-details)
│   ├── FloorController.cs          # フロア情報 (/floors)
│   ├── PointController.cs          # ポイント情報・制御 (/points)
│   ├── PointDetailController.cs    # ポイント詳細 (/point-details)
│   ├── SpaceController.cs          # スペース情報 (/spaces)
│   └── TelemetryController.cs      # テレメトリデータ (/telemetries)
├── Filters/                    # フィルター
│   └── AuthorizeFilter.cs          # 認可フィルター
├── Middlewares/                 # カスタムミドルウェア
│   ├── BasicAuthenticationMiddleware.cs  # Basic 認証（開発環境用）
│   └── Logger/
│       ├── LoggerMiddleware.cs           # リクエスト/レスポンスログ
│       └── LoggerMiddlewareOption.cs
├── Modules/                    # DI モジュール
│   └── EnvModule.cs                # 環境変数ベースの設定
├── Startup/                    # アプリケーション初期化
│   ├── Startup.cs                  # サービス登録
│   ├── TestAuthenticationHandler.cs # テスト用認証ハンドラー
│   ├── IServiceCollectionExtension.Auth.cs     # Azure AD / Basic 認証の DI
│   ├── IServiceCollectionExtension.Cors.cs     # CORS 設定の DI
│   └── IServiceCollectionExtension.Swagger.cs  # Swagger/OpenAPI の DI
├── Program.cs                  # エントリーポイント
└── Dockerfile                  # Docker ビルド定義
```

## 起動プロファイル

| プロファイル | 用途 |
|-------------|------|
| `WithLocal` | ローカル開発 |
| `WithProduction` | 本番データでの開発（GUTP 環境） |
| `WithEng2Production` | 本番データでの開発（工学部2号館環境） |

```bash
dotnet run --launch-profile WithLocal
```

## 認証

| 環境 | 認証方式 | 設定箇所 |
|------|---------|---------|
| 開発環境 | テスト用ハンドラー（常に認証成功） | `TestAuthenticationHandler.cs` |
| 本番環境 | Azure AD (Bearer Token) | `Startup.cs` |

開発・本番ともに `BasicAuthenticationMiddleware` が Swagger / ReDoc パスの保護に使用されます。

## 今後の設計拡張（例）

- ビジネスロジックが複雑化し、Controller が Fat化してきたタイミングで UseCase を作成し、Controller へ DI
- データの種類ごとに Repository を作成し、Controller へ DI
- 接続先（データソース）が増えたタイミングで Adapter を作成し、Repository へ DI
