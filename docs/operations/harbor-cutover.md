# Harbor Cutover Runbook: ACR → Harbor

## Overview

This runbook covers the phased migration from Azure Container Registry (ACR) to Harbor.
The migration uses dual-push (ACR + Harbor simultaneously) so that you can validate Harbor
before pointing deployments at it.

## Prerequisites

- Harbor instance running (local: `docker-compose.harbor.yaml`, K8s: Harbor Helm chart)
- Harbor project `buildingos` created
- Harbor robot account created (see below)
- GitHub repository secrets configured

## Phase 1 — Enable Dual Push (ACR + Harbor)

### 1. Create Harbor robot account

1. Open Harbor UI → Administration → Robot Accounts → New Robot Account
2. Name: `buildingos-ci`
3. Expiration: set to your CI rotation policy (e.g., 90 days)
4. Permissions: `Push`, `Pull` on project `buildingos`
5. Copy the generated secret (shown once)

### 2. Add GitHub repository secrets & variables

In GitHub → Settings → Secrets and variables → Actions:

| Type | Name | Value |
|------|------|-------|
| Secret | `HARBOR_ROBOT_NAME` | `robot$buildingos-ci` |
| Secret | `HARBOR_ROBOT_SECRET` | `<token from step above>` |
| Variable | `HARBOR_REGISTRY` | `harbor.example.com` (your Harbor hostname) |

### 3. Verify dual push workflow

Merge a commit to `main` or manually trigger `harbor-push.yml` and confirm:
- GHCR: `ghcr.io/takashikasuya/buildingos-api-server:main` pushed
- Harbor: `harbor.example.com/buildingos/api-server:main` pushed

Check Harbor UI for vulnerability scan results (Trivy runs automatically).

### 4. Validate image digest parity

```bash
# Check GHCR digest
docker manifest inspect ghcr.io/takashikasuya/buildingos-api-server:main \
  --verbose | grep digest

# Check Harbor digest
docker manifest inspect harbor.example.com/buildingos/api-server:main \
  --verbose | grep digest
```

Both digests should match (same build context, same layer cache).

## Phase 2 — Switch Deployments to Harbor

### Update Helm values (per environment)

```yaml
# kubernetes/helm/building-os/values-<env>.yaml
image:
  registry: harbor.example.com
  repository: buildingos/api-server
  pullSecretName: harbor-pull-secret
```

### Create image pull secret in K8s

```bash
kubectl create secret docker-registry harbor-pull-secret \
  --docker-server=harbor.example.com \
  --docker-username='robot$buildingos-ci' \
  --docker-password='<robot secret>' \
  -n building-os
```

### Rolling restart

```bash
kubectl rollout restart deployment/api-server -n building-os
kubectl rollout status deployment/api-server -n building-os
```

## Phase 3 — Disable ACR Push

Once all environments pull from Harbor without issues for 2+ weeks:

1. Remove the ACR login step from `.github/workflows/main_gutp-build-api-server.yaml`
2. Remove `az acr login` from `Tools/build-and-push-api-server.bash`
3. Remove ACR-related variables from GitHub Actions environments
4. (Optional) Disable the ACR in Azure Portal to stop billing

## Local Development

Start Harbor locally:

```bash
docker compose -f docker-compose.harbor.yaml up -d
# Harbor UI: http://localhost:8080 (admin / Harbor12345)
```

Push a local build to Harbor:

```bash
REGISTRY=localhost:8080/buildingos IMAGE_TAG=dev \
  ./Tools/build-and-push-api-server.bash
```

Or dual-push to both registries:

```bash
ACR_REGISTRY=your-acr.azurecr.io \
HARBOR_REGISTRY=localhost:8080/buildingos \
  ./Tools/build-and-push-api-server.bash
```

## Failback Procedure

If Harbor is unavailable, revert deployments to pull from GHCR:

```bash
# Update Helm values to use GHCR
helm upgrade building-os kubernetes/helm/building-os \
  --set image.registry=ghcr.io \
  --set image.repository=takashikasuya/buildingos-api-server \
  -n building-os
```

GHCR images are always available as long as GITHUB_TOKEN secrets are valid.
