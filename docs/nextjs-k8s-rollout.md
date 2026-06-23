# Next.js Kubernetes Rollout

This document covers the OSS deployment path for `web-client` and
`admin-console`.

## Deployment Model

Both Next.js applications are built as containers and deployed through
`kubernetes/helm/building-os`.

| App | Service | OIDC client |
|---|---|---|
| `web-client` | `web-client` | `web-client` |
| `admin-console` | `admin-console` | `admin-console` |

Ingress is handled by Traefik `IngressRoute` resources in the umbrella chart.
Runtime authentication is Keycloak OIDC only.

## Environment

Required frontend variables:

| Variable | Purpose |
|---|---|
| `NEXT_PUBLIC_KEYCLOAK_AUTHORITY` | Keycloak realm issuer URL |
| `NEXT_PUBLIC_KEYCLOAK_CLIENT_ID` | `web-client` or `admin-console` |
| `NEXT_PUBLIC_API_BASE_URL` | Browser-visible API base URL |

## Validation

Before rollout:

```bash
helm lint kubernetes/helm/building-os
helm template building-os kubernetes/helm/building-os -f kubernetes/helm/building-os/values-prod.yaml
```

After rollout:

- Access the web client through the Traefik host.
- Complete Keycloak sign-in.
- Confirm API calls carry `Authorization: Bearer <token>`.
- Confirm admin console sign-in and user/group management screens load.

## Rollback

Use Helm history and rollback:

```bash
helm history building-os -n building-os
helm rollback building-os <REVISION> -n building-os --wait --timeout=10m
```

If image tag automation updated Argo CD values, revert the image tag commit or
set the previous tag in `argocd/values/<env>.yaml` and allow Argo CD to sync.

## SWA Parity

Azure Static Web Apps parity is intentionally not part of the OSS-only target.
The accepted parity check is K8s + Traefik + Keycloak OIDC behavior for both
Next.js apps.

