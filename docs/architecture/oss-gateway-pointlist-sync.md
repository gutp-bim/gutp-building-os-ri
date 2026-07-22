# Gateway Point List 同期 API（#224）

Integration Gateway（別リポ `nexus-gateway`）が、自分が所有する point list の**正本（Building OS の
twin = OxiGraph `sbco:PointExt`）**に追従するための provisioning API。gateway は native addressing
（BACnet object/instance 等）→ `point_id` をローカル解決するため、twin との不一致は `accepted < sent`
（テレメトリ欠落・制御不能）として顕在化する。本 API でその追従口を提供する。

> 関連: #181（point list 共有）、`docs/architecture/gateway-bridge-ingress-egress-split.md`、
> `docs/operations/oss-gateway-bridge-infra.md`（mTLS ingress）。整合性の計測は E5（#243）。

## エンドポイント

```
GET /gateways/{gatewayId}/pointlist
```

当該 `gatewayId` が所有する（`sbco:gatewayId`）全 point を返す。各 point は twin が保持する範囲で
native addressing / unit / writable / control schema / device を含む（無いものは null）。

### レスポンス（200, `GatewayPointListResponse`）

```jsonc
{
  "gatewayId": "GW001",
  "revision": "\"sha256:...\"",     // = ETag 値（内容ハッシュ）
  "generatedAt": "2026-06-14T00:00:00Z",
  "points": [
    {
      "pointId": "PT001",            // sbco:id（線に載る正準 ID）
      "localId": "LOCAL001",         // sbco:localId（MQTT/Hono の局所キー等、あれば）
      "native": {                     // BACnet native addressing（あれば）
        "protocol": "bacnet",
        "deviceId": "BAC001",        // sbco:deviceIdBacnet
        "objectType": "Analog-Input",// sbco:objectTypeBacnet
        "instanceNo": "1001"          // sbco:instanceNoBacnet
      },
      "unit": "C",                   // sbco:unit
      "writable": true,              // sbco:writable
      "controlSchema": {              // bos:dataType / minValue / maxValue / enumLabels（あれば）
        "dataType": "number", "minValue": "16", "maxValue": "30", "enumLabels": null
      },
      "device": { "dtId": "...", "id": "DEV1", "name": "AC-1" }
    }
  ]
}
```

- 所有 point ゼロ → `200` + `points: []`（404 にしない）。
- `gatewayId` 空 → `400`。

### 版管理（ETag / 304）

レスポンスは内容ハッシュ ETag（`"sha256:..."`）を返す。pointId 安定ソートで正準化してから SHA-256
を取るため**順序非依存**で、内容が変わった時だけ ETag が変わる。

- `Cache-Control: no-cache`（常に ETag で再検証）。
- gateway は `If-None-Match: "sha256:..."` を付けて安価にポーリングし、**一致なら 304**、変化時のみ
  全体取得する。
- 通常の200応答で計算したETagは NATS KV `pointlist-revision` にGateway単位で保存する。次の条件付き取得は
  共有ETagだけを読み、合致すればOxiGraph全Point検索とレスポンス再構築を行わず304を返す。

#### キャッシュ失効と複数APIインスタンスの整合性

- リビジョンはper-replicaメモリではなくNATS KVに保存するため、どのApiServer replicaが200を生成しても
  他replicaが同じETagを304判定に利用できる。
- Twin Adminのappend/replaceは、OxiGraph変更前に共有世代を`updating`へ変更し、変更完了後に新しい
  `stable`世代へ切り替える。各GatewayのETagは生成時の世代を持ち、現行`stable`世代と一致するものだけを
  信頼する。変更中、NATS障害時、世代不一致時はキャッシュを使わずOxiGraphから200応答を再構築する。
- 世代変更はNATS KV revisionのCASと更新トークンで排他制御し、複数APIからの同時importは拒否する。
- 変更前の世代更新に失敗した場合、Twin Admin import自体を中止する。変更後の世代確定に失敗した場合は
  `updating`のままなので304を返さず、安全側へフォールバックする。
- 起動時はseed処理の後に共有世代を更新するため、以前のプロセスが保存したETagは再利用しない。
- サポートされる更新経路はTwin Admin importと起動時seedである。OxiGraphを外部から直接更新した場合は
  整合性契約の対象外なので、全ApiServerを再起動して共有世代を失効させる。

## 認証（マシン認証・本番品質の肝）

ユーザー RBAC ではなく **gateway のマシン認証**。gateway は既に gRPC（GatewayEgress）を
**per-gateway mTLS** で接続している（`docs/operations/oss-gateway-bridge-infra.md`）。同じ仕組みを REST にも適用する。

- mTLS 終端の ingress（Traefik）が、検証済みクライアント証明書の identity を**信頼ヘッダ**
  （既定 `X-Gateway-Id`）に注入する。
- エンドポイントは `X-Gateway-Id == {gatewayId}` を要求する（`IGatewayIdentityResolver`）。
- ops 診断用に **admin JWT はバイパス**（`idtyp=app` / admin role）。
- 本ルートは `[AuthorizeFilter]` を付けない（gateway は user JWT を持たないため）。束縛不一致かつ
  非 admin は **403**。

### トラスト境界（必須）

`X-Gateway-Id` は**信頼ヘッダ**である。安全性は以下に依存する:

1. 当該ルートは **mTLS を強制する ingress 経由のみ**到達可能にする。
2. ingress は**クライアント証明書から** `X-Gateway-Id` を設定し、**外部から来た同名ヘッダは必ず除去**する
   （ヘッダ・スプーフィング防止）。
3. ApiServer を ingress を介さず直接公開しない。

### Traefik 設定例（HITL: on-cluster 検証は人手）

```yaml
# TLSOption: クライアント証明書を必須・検証
apiVersion: traefik.io/v1alpha1
kind: TLSOption
metadata: { name: gateway-mtls }
spec:
  clientAuth:
    clientAuthType: RequireAndVerifyClientCert
    secretNames: [gateway-client-ca]
---
# Middleware: 証明書 subject(CN) を信頼ヘッダへ。外部からの X-Gateway-Id は通さない経路に限定。
apiVersion: traefik.io/v1alpha1
kind: Middleware
metadata: { name: gateway-id-from-cert }
spec:
  passTLSClientCert:
    info:
      subject: { commonName: true }
# passTLSClientCert は X-Forwarded-Tls-Client-Cert-Info を付与する。CN→X-Gateway-Id への単純化は
# 軽量プラグイン/サイドカー、または CN をそのまま gateway_id とする運用で対応（要 infra レビュー）。
```

## on-cluster 検証チェックリスト（HITL）

1. gateway 用クライアント CA / 証明書を発行（cert-manager 可）。CN = gateway_id。
2. ApiServer の `/gateways/*/pointlist` ルートに mTLS TLSOption + passTLSClientCert Middleware を適用。
3. 証明書なしのアクセスが拒否されること、別 gateway 証明書で 403 になることを確認。
4. `If-None-Match` で 304 が返ること、twin 更新後に ETag が変わり 200 で差分が反映されることを確認。

## 差分配布（`?since=`、#224/diff）

```
GET /gateways/{gatewayId}/pointlist?since={etag}
```

大規模サイトの全取得を回避するため、クライアントの最後に取得した ETag からの差分を返す。

- `since == 現在の ETag` → **304**（変化なし）。
- `since` のスナップショットを保持していれば → **200** `GatewayPointListDiffResponse`
  （`full:false` / `added[]` / `removed[](pointId)` / `changed[]`）。
- スナップショット未保持（退避・replica 再起動）→ **200** `full:true` + `points[]`（全件、クライアントは置換）。

ETag は内容ハッシュで履歴を持たないため、直近のスナップショットを **ETag でキャッシュ**
（`IGatewayPointListSnapshotStore`、既定 IMemoryCache・TTL 1h・per-replica・ベストエフォート）し、
現在の point list と pure な `PointListDiffer` で差分（identity=pointId、changed=内容差）を計算する。
差分は最適化であり、未保持時の full フォールバックが安全な既定動作。

## push 通知（`#224/push`）

twin の point list 更新を、接続中の gateway に **GatewayEgress ストリーム経由で近リアルタイム配信**する。
ポーリング間隔を待たずに再同期できる。

- proto: `EgressDown` は `oneof { ControlCommand command=1; PointListUpdate point_list_update=2; }`。
  field 1 を維持しているので ControlCommand のみの旧 BOWS と後方互換。`PointListUpdate{gateway_id, revision}`。
- subject: `building-os.pointlist.updated.gw.{gatewayId}`（`EgressSubjects.PointListUpdate`）。
  当該 gateway を持つ GatewayBridge replica が購読し `EgressDown{PointListUpdate}` を下流へ流す。
- publisher: `IPointListUpdatePublisher`（`NatsPointListUpdatePublisher`, Shared）。
- trigger: `OxiGraphSeedHostedService` が seed 成功後に所有 gateway へ per-gateway 発行（best-effort・
  publisher 未注入なら skip）。revision は空で、gateway は最後の ETag で `GET .../pointlist` を再検証する
  （push は最適化、信頼性は ETag ポーリングが担保。取りこぼしても次ポーリングで収束）。

## スコープ外（後続）

- gateway 側の受信処理（別リポ `nexus-gateway`）。
- `PointListUpdate.revision` に実 ETag を載せる（現状は空シグナル + gateway 側 ETag 再検証）。
