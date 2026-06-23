# 制御系安全分界ドキュメント — Building OS OSS

Issue #73 成果物

> **HITL 必須**: 本ドキュメントの内容（フェールセーフ設定値・タイムアウト値・監査要件）は
> 制御対象機器の安全責任者によるレビューとサインオフが必要です。
> PR コメントに「HITL レビュー済み / 担当者 / 日付」を記録してください。

---

## 1. 制御権限モデル

### 誰が `building-os.control.request` を publish できるか

```
API Client → POST /points/{pointId}/control
              │
              ▼ (AuthorizeFilter)
         Keycloak JWT 検証
              │
              ├─ IsAdmin=true → 全ポイント制御可
              └─ IsAdmin=false → CanWritePointAsync() チェック
                                  │
                                  └─ IAuthorizedTwinView
                                      (permission: "point:{hashed_id}:write")
```

- 認証は `AuthorizeFilter` が全エンドポイントに適用する（`DISABLE_AUTH=true` を除く）
- ローカル開発環境では `DISABLE_AUTH=true` で認証をスキップできるが、
  本番環境では必ず `false` とすること
- Keycloak ロールの種類:

| ロール | 制御権限 |
|--------|---------|
| `admin` | 全ポイント |
| `building_operator` | 担当ビルのポイント（permission string で管理） |
| `viewer` | 読み取りのみ（制御不可） |

### Permission String 形式

```
p:{56hex}:w
```

`PermissionHelper` がリソース種別 (`point→p`)、SHA-256 先頭 56 文字のハッシュ、アクション (`write→w`) を組み合わせて生成する。
`IAuthorizationService.CanAccessAsync()` が permission store と照合する。

---

## 2. 制御フロー

```
API Server
  POST /points/{id}/control
    → PointController.Control()
    → IPointControlCommandPublisher.PublishAsync()
    → NATS (building-os.control.request)
                  │
                  ▼
         NatsPointControlWorker
                  │
          ┌───────┴───────┐
          │               │
  KandtDeviceControl   HonoDeviceControl
  Handler              Handler
          │               │
          ▼               ▼
   Azure IoT Hub      Eclipse Hono
   → Kandt GW(BACnet) AMQP Northbound
          │               │
          └───────┬───────┘
                  │ NATS (building-os.control.result)
                  ▼
         NatsControlResultBus
                  │
                  ▼
     gRPC PointControlService.WaitForResult()
                  │
                  ▼
              UI (Web Client)
```

---

## 2.5 入力値バリデーション（#153）

writable ゲート（#139）は**認可**（誰がそのポイントを制御できるか）を担保するが、**入力値の妥当性**は
別問題。`POST /points/{pointId}/control` は publish 前に、ポイントの **ControlSchema**（ポイントリスト=
source of truth）に対して値を検証し、不正なら **400** を返す（`PointController.Control` →
`IControlSchemaResolver.ResolveAsync` → `ControlValueValidator.Validate`）。

| DataType | 検証 |
|---|---|
| `boolean` | 値は `0` または `1` |
| `enum` | 値は `EnumLabels` のキー集合（許容コード）のいずれか |
| `number` | `MinValue` ≤ 値 ≤ `MaxValue`（設定された側のみ） |

**スキーマ未定義のポイントは許容（検証スキップ）** — 後方互換。認可は writable ゲートが担保する。
スキーマを制約したいポイントには必ず ControlSchema を付与すること（HITL レビュー対象）。

> フェイルオープン: スキーマ解決自体が失敗した場合（OxiGraph 一時障害等）も検証はスキップされ、
> 制御は writable ゲートのみで通る（twin 障害で全制御が止まるのを避ける選択）。解決失敗は警告ログに
> 残るため、障害は可視。値検証を厳格にしたい運用ではこのフェイルオープンを許容範囲か HITL で確認すること。

ControlSchema は OxiGraph（ポイントリスト）上の `sbco:PointExt` の Building OS 拡張プロパティで表現する:

```turtle
<...PT-AC-MODE> a sbco:PointExt ;
  sbco:id "PT-AC-MODE" ;
  sbco:writable "true" ;
  bos:dataType  "enum" ;
  bos:enumLabels "{\"1\":\"冷房\",\"2\":\"暖房\",\"3\":\"送風\"}" .

<...PT-AC-SETTEMP> a sbco:PointExt ;
  sbco:id "PT-AC-SETTEMP" ;
  sbco:writable "true" ;
  bos:dataType "number" ;
  bos:minValue "16" ;
  bos:maxValue "30" .
```

> `bos:` = `http://buildingos.gutp.jp/ontology#`。`ControlType`（送信プロトコル）の動的解決は
> ゲートウェイ接続レジストリ（#154）が担い、Hono 固定は解消済み。

---

## 3. フェールセーフ設定

> **HITL**: 以下の設定値は機器の安全動作に直結します。
> 担当安全責任者が実機の仕様書に基づいて確認・承認してください。

| 機器種別 | タイムアウト時のフェールセーフ挙動 | 根拠 |
|---------|----------------------------------|------|
| HVAC (Hono / Kandt 経由) | 自動運転モードを維持（制御コマンドを送信しない） | 空調の急停止は室内環境に影響するため |
| Lighting (BACnet) | 現在の状態を維持 | 消灯による安全リスク回避 |
| Power Meter (読み取り専用) | N/A | 制御不可 |
| 汎用 BACnet ポイント | HITL 確認が必要 | 機器依存 |

### NatsPointControlWorker のタイムアウト

- `building-os.control.request` の NATS メッセージ ACK タイムアウト: **30 秒**
- handler が応答しない場合: `PointControlResult.Failed` をバスに通知
- gRPC `WaitForResult` のサーバ側タイムアウト: **30 秒** (`PointControlGrpcService.Timeout`)
- クライアントがタイムアウト前に切断した場合: handler 処理はキャンセルされない
  （`CancellationToken` を handler に伝播させること — 将来課題）

---

## 4. 二重制御の抑止

現在の実装では API レベルの二重制御抑止は行っていない。
以下のいずれかを実装することを推奨する（将来課題, HITL 要レビュー）:

1. **Idempotency Key**: クライアントが `requestId` を指定し、重複は `point_control_audit` で弾く
2. **Point ロック**: 制御中のポイントへの追加コマンドを 409 Conflict で拒否
3. **Rate Limit**: 同一ポイントへの制御は N 秒に 1 回まで（`IMemoryCache` で実装可能）

---

## 5. 監査ログ

### テーブル: `point_control_audit`

```sql
CREATE TABLE point_control_audit (
    id           UUID        PRIMARY KEY,
    point_id     TEXT        NOT NULL,
    request      JSONB       NOT NULL,   -- ControlType + Body
    result       JSONB,                  -- { status, response }
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ
);
```

マイグレーション: `DotNet/BuildingOS.Shared/Migrations/Timescale/V002__point_control_audit.sql`

### 記録タイミング

| イベント | 記録内容 |
|---------|---------|
| コマンド受信時 | `INSERT` — id, point_id, request, created_at |
| handler 完了時 | `UPDATE` — result, completed_at |
| handler タイムアウト時 | `UPDATE` — result=`{status: "timeout"}`, completed_at |

### 保持期間

- **1 年間** PostgreSQL に保持
- 1 年超の記録は MinIO (Cold) にアーカイブ後に削除（`ControlAuditArchiveJob` — 将来実装）

---

## 6. E2E S6 テスト計画

### 実行方法

```bash
cd Tools/e2e-performance

# API Server と docker-compose を起動してから実行
k6 run k6/s6_point_control.js \
  -e BASE_URL=http://localhost:5000 \
  -e CONTROL_POINT_ID=sim-control-point-001 \
  -e TEST_RUN_ID=$(date +%Y%m%dT%H%M%SZ)-s6

# 結果を results/ に保存する場合
k6 run k6/s6_point_control.js --out json=results/$(date +%Y%m%dT%H%M%SZ)-s6/k6-output.json
```

### テスト観点

| 観点 | 検証方法 |
|------|---------|
| 往復レイテンシ p95 | k6 `control_duration_ms` メトリクス |
| タイムアウト挙動 | handler を停止した状態で `timeout_probe` シナリオを実行 |
| 制御失敗時の `result.error` 伝搬 | `point_control_audit` テーブルを直接確認 |
| gRPC stream back | Playwright (`Tools/e2e-performance/playwright/`) で UI 受信を目視確認 |

### Acceptance Criteria

- [ ] S6 ラン 1 回以上 PASS（p95 < 3000 ms、エラー率 < 1%）
- [ ] タイムアウト時に `point_control_audit.result` に `{status: "timeout"}` が記録される
- [ ] gRPC stream back を Web Client が受信できることを確認（手動検証で可）
- [ ] `PERFORMANCE_SUMMARY.md` の S6 行を更新
- [ ] HITL レビューサインオフが PR コメントに記録

---

## 7. 関連ドキュメント

- `proto/point_control.proto` — gRPC サービス定義
- `DotNet/BuildingOS.ApiServer/Services/PointControlGrpcService.cs` — WaitForResult 実装
- `DotNet/BuildingOS.ApiServer/Controllers/PointController.cs` — REST エンドポイント
- `DotNet/BuildingOS.Shared/Migrations/Timescale/V002__point_control_audit.sql` — 監査テーブル
- `docs/oss-tier-architecture.md` — Hot/Warm/Cold データフロー全体
