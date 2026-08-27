# Argo CD GitOps Guide

Building OS の GitOps 運用ガイド。同梱の参照環境 `reference` を起点に Argo CD で継続的デプロイを実現する。

## Architecture

```
git push to main
      ↓
harbor-push.yml — builds only the images whose sources changed,
                  publishes ghcr.io/gutp-bim/buildingos-<service>:sha-<short-sha>
                  (and :main)
      ↓  workflow_run: completed && conclusion == success
argocd-image-update.yml — rewrites `image.tag` to sha-<short-sha>
                          in the overlay of each service that was built
      ↓
git commit to main ([skip ci])
      ↓
Argo CD detects diff → helm upgrade (auto sync)
      ↓
building-os namespace updated
```

> **タグは、そのタグを持つイメージが実際に publish された後にしか動きません。**
> `argocd-image-update` は harbor-push の**成功後にのみ**起動し、その run が実際に
> ビルドしたサービスの overlay だけを書き換えます。以前は `push` にも独立して
> トリガされていたため、ビルドが 1 度も成功していない期間にタグだけが 88 回進み、
> 存在しないイメージを指し続けていました（#306）。
>
> タグ形式は harbor-push が publish するもの（`sha-` + 短縮 SHA 7 桁）と一致させて
> あります。`sha-` が無い・8 桁、といったズレはそのまま「存在しないタグ」になります。

## Initial Setup

### 1. Install Argo CD

```bash
helm repo add argo https://argoproj.github.io/argo-helm
helm repo update
helm upgrade --install argocd argo/argo-cd \
  --namespace argocd \
  --create-namespace \
  -f argocd/install/argocd-values.yaml
```

### 2. Apply the Application manifest

```bash
kubectl apply -f argocd/apps/reference.yaml
```

### 3. Verify sync

```bash
argocd app get building-os-reference
argocd app sync building-os-reference  # manual sync for first deploy
```

## Rollback Procedure

Argo CD tracks `revisionHistoryLimit: 5` prior synced states.

### Option A: git revert (recommended)

```bash
# Find the commit that changed the broken image tag
git log argocd/values/reference.yaml --oneline | head -5

# Revert to the last known-good tag
git revert <commit-sha>
git push origin main
# Argo CD detects the push and auto-syncs within ~3 minutes
```

### Option B: Argo CD UI / CLI rollback

```bash
# Roll back to a previous revision without touching git
argocd app rollback building-os-reference <revision-number>
```

Note: Option B creates drift between git state and cluster state. Always follow up with a git revert to reconcile.

## Keycloak SSO Integration

Argo CD is pre-configured with an OIDC placeholder in `argocd/install/argocd-values.yaml`. After Keycloak is provisioned (Issue #10):

1. Create a Keycloak client `argocd` in the `building-os` realm
2. Set redirect URIs: `https://argocd.buildingos.local/auth/callback`
3. Create groups: `building-os-admins`, `building-os-ops`
4. Update `oidc.config.issuer` in `argocd-values.yaml` with the actual Keycloak realm URL
5. Create K8s secret:
   ```bash
   kubectl create secret generic argocd-oidc-secret \
     -n argocd \
     --from-literal=oidc.keycloak.clientSecret=<client-secret>
   ```
6. `helm upgrade` Argo CD with the updated values

## Multi-Environment Expansion (ApplicationSet — Post Phase 6)

Once the single-env pattern is stable, expand using Argo CD ApplicationSet:

```yaml
# argocd/appsets/building-os.yaml (future)
apiVersion: argoproj.io/v1alpha1
kind: ApplicationSet
metadata:
  name: building-os
  namespace: argocd
spec:
  generators:
    - list:
        elements:
          - env: reference
            cluster: https://kubernetes.default.svc
          - env: staging
            cluster: https://k8s-staging.example.com:6443
          - env: production
            cluster: https://k8s-production.example.com:6443
  template:
    metadata:
      name: 'building-os-{{env}}'
    spec:
      source:
        path: kubernetes/helm/api-server
        helm:
          valueFiles:
            - ../../argocd/values/{{env}}.yaml
      destination:
        server: '{{cluster}}'
        namespace: building-os
      syncPolicy:
        automated:
          prune: true
          selfHeal: true
```

Migration path:
1. Add `argocd/values/<env>.yaml` for each new environment
2. Add the environment to the ApplicationSet generator list
3. Register the remote cluster: `argocd cluster add <context>`

## Directory Structure

```
argocd/
├── install/
│   └── argocd-values.yaml     # Argo CD Helm install values
├── apps/
│   └── reference.yaml       # Single Application (Phase 6)
├── values/
│   └── reference.yaml       # Per-env values overlay
└── appsets/                   # ApplicationSet (post-Phase 6)
```
