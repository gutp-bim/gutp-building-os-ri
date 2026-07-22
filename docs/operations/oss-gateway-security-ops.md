# Gateway Security Operations — 証明書・mTLS・gateway_id 束縛の運用

ゲートウェイ（BOWS / nexus-gateway）は Building OS に **mTLS マシン認証**で接続します（ユーザ RBAC ではない）。
本書はその証明書発行・ローテーション・失効と、`gateway_id` ↔ 証明書 identity の束縛、信頼境界の運用手順を
1 枚にまとめます。設計の全体像は [gateway-integration.md](../guides/gateway-integration.md)、デプロイ配置は
[oss-production-deployment.md](oss-production-deployment.md)、インフラ配線は
[oss-gateway-bridge-infra.md](oss-gateway-bridge-infra.md) を参照。

> ⚠️ 現状有姿（AS IS）・無保証。on-cluster の mTLS 終端・証明書発行・ヘッダ注入の疎通検証は **HITL**（人手）。
> 本書は手順とチェックリストで、コード側（束縛判定・信頼ヘッダ解決）は単体検証済み
> （[#296](https://github.com/takashikasuya/gutp-building-os-oss/issues/296) / #224）。

---

## 0. 全体像（何を守るか）

| 経路 | サービス | 認証 | 束縛 |
|---|---|---|---|
| テレメトリ ingress | ConnectorWorker `GatewayIngress`（gRPC :5051） | mTLS（client CA）→ 信頼ヘッダ | `frame.gateway_id == X-Gateway-Id`（#296, enforce 切替） |
| 制御 egress | GatewayBridge `GatewayEgress`（gRPC :5052） | mTLS（client CA） | per-gateway NATS routing |
| point list 同期 | ApiServer `GET /gateways/{id}/pointlist` | mTLS → 信頼ヘッダ | `header X-Gateway-Id == path {id}`（#224） |

3 経路すべてが **同一のクライアント CA と同一の信頼ヘッダ（`X-Gateway-Id`）** を共有します。証明書 1 枚で
3 経路を同時に運用できるよう、**CN（または SAN）= `gateway_id`** を発行ポリシーの原則にします。

---

## 1. 証明書の階層と発行（cert-manager）

```
(Root/Intermediate CA)            ← 組織の PKI（任意）
        │
        ▼
 gateway-client-ca (Issuer)       ← gateway クライアント証明書を署名する CA。Secret: gateway-client-ca
        │  cert-manager Certificate（CN = gateway_id）
        ▼
 gw-<id> client cert (mTLS)       ← 各ゲートウェイへ配布（edge）
```

- **サーバ証明書**（ingress ホスト）: cert-manager `Certificate` → `serverSecretName`（公開 ACME でも内部 CA でも可）。
- **クライアント CA**: gateway 証明書を署名する CA を `gateway-client-ca` Secret に置き、Traefik `TLSOption` の
  `clientAuth.secretNames` で参照。
- **ゲートウェイ証明書**: cert-manager `Certificate`（`issuerRef` = 上記 CA）で **CN = `gateway_id`** を発行。

### cert-manager Certificate 例（gateway 1 台）

```yaml
apiVersion: cert-manager.io/v1
kind: Certificate
metadata:
  name: gw-gw001
  namespace: building-os
spec:
  secretName: gw-gw001-mtls          # edge へ配布（tls.crt / tls.key）
  duration: 2160h                    # 90 日
  renewBefore: 360h                  # 15 日前に自動更新
  commonName: GW001                  # = gateway_id（束縛の要）
  subject:
    organizations: [building-os-gateways]
  usages: [client auth, digital signature, key encipherment]
  issuerRef:
    name: gateway-client-ca
    kind: Issuer
```

> **命名規則を固定する**こと。`gateway_id` は twin（`sbco:gatewayId`）で建物内一意（seed import で検証）。
> 証明書 CN をこの値に厳密一致させる（大文字小文字も込み — 束縛比較は Ordinal、[#296](https://github.com/takashikasuya/gutp-building-os-oss/issues/296)）。

---

## 2. `gateway_id` ↔ 証明書 identity の束縛

ingress（Traefik）が検証済み証明書 subject を信頼ヘッダ `X-Gateway-Id` に注入し、アプリがフレーム/パスと突合する。

```
gateway cert (CN=GW001) ──mTLS──▶ Traefik TLSOption(RequireAndVerifyClientCert)
                                   + Middleware passTLSClientCert(subject.commonName)
                                   → X-Gateway-Id: GW001
        ┌──────────────────────────────┴───────────────────────────────┐
        ▼                                                                ▼
ConnectorWorker GatewayIngress                                ApiServer /gateways/{id}/pointlist
  GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY=true                   header X-Gateway-Id == path {id}
  → frame.gateway_id == X-Gateway-Id（不一致は skip+meter）     → 不一致かつ非 admin は 403
```

- ヘッダ名は両経路とも `X-Gateway-Id`（変更時は ingress / `GRPC_INGRESS_GATEWAY_ID_HEADER` / #224 設定を**揃える**）。
- `passTLSClientCert` は `X-Forwarded-Tls-Client-Cert-Info` を付与する。CN → `X-Gateway-Id` への単純化は軽量
  プラグイン/サイドカー、または CN をそのまま `gateway_id` とする運用で対応（要 infra レビュー、
  [oss-gateway-pointlist-sync.md](../architecture/oss-gateway-pointlist-sync.md)）。

### Traefik 設定例（ingress 共通）

```yaml
apiVersion: traefik.io/v1alpha1
kind: TLSOption
metadata: { name: gateway-mtls, namespace: building-os }
spec:
  clientAuth:
    clientAuthType: RequireAndVerifyClientCert
    secretNames: [gateway-client-ca]
---
apiVersion: traefik.io/v1alpha1
kind: Middleware
metadata: { name: gateway-id-from-cert, namespace: building-os }
spec:
  passTLSClientCert:
    info:
      subject: { commonName: true }
```

このミドルウェアを **GatewayIngress ルート・pointlist ルートの両方**に適用する。

---

## 3. 信頼境界（必須・最重要）

`X-Gateway-Id` は**信頼ヘッダ**。安全性は次に全面的に依存する:

1. 当該ルートは **mTLS を強制する ingress 経由のみ**到達可能にする（`GRPC_INGRESS_PORT` / egress :5052 /
   ApiServer を ingress を介さず直接公開しない）。
2. ingress は**クライアント証明書から** `X-Gateway-Id` を設定し、**外部から来た同名ヘッダは必ず除去**する
   （ヘッダ・スプーフィング防止）。
3. ingress テレメトリは `GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY=true` で束縛を有効化。重複・異値の信頼ヘッダは
   **fail-closed**（拒否）で扱われる（[#296](https://github.com/takashikasuya/gutp-building-os-oss/issues/296)）。
4. 起動ログで ingress の `identity-binding=enforced/off` を確認する（「listener up なのに enforcement off」検知）。

> 失敗モード: ②を怠ると、攻撃者が自分で `X-Gateway-Id` を送って他 GW になりすませる。①を怠ると mTLS を
> バイパスして h2c リスナーへ直接到達できる。**①②は本機能の前提条件**であり、コードだけでは担保できない。

---

## 4. ローテーション（無停止更新）

- cert-manager の `renewBefore` で**有効期限前に自動再発行**（上例: 90 日 / 15 日前）。`secretName` の中身が
  更新され、edge は新しい鍵を読み込む。
- **CA ローテーション**は段階移行: 新 CA を `clientAuth.secretNames` に**併記**（新旧両方を信頼）→ 全 gateway を
  新 CA 署名証明書へ移行 → 旧 CA を `secretNames` から外す。これで接続断なしに切替できる。
- `gateway_id`（= CN）は**ローテーションで変えない**。変えると twin の `sbco:gatewayId` と束縛が崩れ、point list
  の所有権・ingress 束縛が一斉に不一致になる。id 変更は「新 gateway として seed し直す」運用にする。

---

## 5. 失効（revocation）

mTLS の失効は「もう接続させない」を**速く・確実に**反映することが要点:

1. **CA からの除外（最も確実）**: 当該 gateway を新 CA へは移行せず、旧 CA を `secretNames` から外す段階で締め出す。
2. **証明書の削除**: edge 配布済み Secret（`gw-<id>-mtls`）と cert-manager `Certificate` を削除し、再発行を止める。
3. **アプリ側の即時遮断**: twin から当該 `gateway_id` の point 所有権を外す（seed 再取り込み）。
   - ingress: 所有しない point は `identity`/`ownership` で skip され、テレメトリ注入が止まる。
   - egress: per-gateway subject に何も流さない／pointlist は当該 id に空を返す。
4. （任意）CRL / OCSP を使う場合は ingress（Traefik/Envoy）側で設定。運用簡素化のため本 OSS は**短命証明書 +
   CA 除外**を基本線とする。

> 失効は「証明書」と「twin の所有権」の**両輪**で行う。証明書だけ消しても twin に古い所有権が残ると、別経路の
> 整合が崩れるため。

---

## 6. enforce の段階導入（OFF → 監視 → ON）

本番でいきなり `enforce=true` にすると、ヘッダ注入の配線ミスで**全フレーム拒否**になり得る。段階導入する:

1. **OFF**（既定）で ingress を起動し、テレメトリが流れることを確認。
2. ingress に mTLS + `passTLSClientCert` を適用し、`X-Gateway-Id` が注入されることをログ/メトリクスで確認
   （この段階では束縛は効かない＝既存挙動）。
3. メトリクス `building_os.ingress.messages{result}` を観察し、`identity_mismatch`/`identity_missing` が
   出ないこと（= ヘッダと `gateway_id` が一致）を確認。
4. **ON**（`GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY=true`）に切替。起動ログで `identity-binding=enforced` を確認。
5. 切り戻しは env を外して recreate（即時 OFF）。

---

## 7. on-cluster 検証チェックリスト（HITL）

- [ ] クライアント CA（`gateway-client-ca`）と gateway 証明書（CN=gateway_id）を cert-manager で発行。
- [ ] Traefik `TLSOption`（RequireAndVerifyClientCert）+ `passTLSClientCert` Middleware を ingress /
      pointlist の各ルートへ適用。**非信頼経路で `X-Gateway-Id` を除去**。
- [ ] 証明書なしのアクセスが拒否される（mTLS 必須）。
- [ ] 別 gateway 証明書での pointlist アクセスが **403**、ingress 送信が **skip（identity_mismatch）**。
- [ ] `GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY=true` の起動ログに `identity-binding=enforced` が出る。
- [ ] ローテーション（CA 併記 → 移行 → 旧 CA 除外）が接続断なしで通る。
- [ ] 失効（CA 除外 + twin 所有権剥奪）でテレメトリ/制御が止まる。

---

## 8. 関連

- [gateway-integration.md](../guides/gateway-integration.md) — 接続モデル（ingress/egress・point list 同期・mTLS §4）
- [oss-gateway-bridge-infra.md](oss-gateway-bridge-infra.md) — Traefik/mTLS/cert-manager/ArgoCD 配線（HITL）
- [oss-gateway-pointlist-sync.md](../architecture/oss-gateway-pointlist-sync.md) — pointlist 同期 API の認証・信頼境界
- [oss-production-deployment.md](oss-production-deployment.md) — 本番デプロイ配置・ネットワーク境界
- 環境変数: [ルート README](../README.md) / [CLAUDE.md](../../CLAUDE.md)（`GRPC_INGRESS_REQUIRE_GATEWAY_IDENTITY` ほか）
