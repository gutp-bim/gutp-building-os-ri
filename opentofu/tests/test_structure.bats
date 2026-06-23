#!/usr/bin/env bats
# Structural tests for OpenTofu modules and Helm charts.
# Run with: bats opentofu/tests/test_structure.bats

REPO_ROOT="$(cd "$(dirname "$BATS_TEST_FILENAME")/../.." && pwd)"
TOFU_DIR="$REPO_ROOT/opentofu"
HELM_DIR="$REPO_ROOT/kubernetes/helm"

# ── OpenTofu module structure ─────────────────────────────────────────────────

@test "opentofu: providers.tf exists and declares required_version" {
  grep -q "required_version" "$TOFU_DIR/providers.tf"
}

@test "opentofu: providers.tf declares kubernetes provider" {
  grep -q "hashicorp/kubernetes" "$TOFU_DIR/providers.tf"
}

@test "opentofu: providers.tf declares helm provider" {
  grep -q "hashicorp/helm" "$TOFU_DIR/providers.tf"
}

@test "opentofu: providers.tf declares S3 (MinIO) backend" {
  grep -q 'backend "s3"' "$TOFU_DIR/providers.tf"
}

@test "opentofu: main.tf instantiates all required modules" {
  for mod in timescaledb minio nats oxigraph harbor hono api_server connector_worker monitoring; do
    grep -q "module \"$mod\"" "$TOFU_DIR/main.tf"
  done
}

@test "opentofu: all modules have main.tf, variables.tf, outputs.tf" {
  for mod in timescaledb minio nats oxigraph harbor hono api-server connector-worker web-client monitoring; do
    [ -f "$TOFU_DIR/modules/$mod/main.tf" ]
    [ -f "$TOFU_DIR/modules/$mod/variables.tf" ]
    [ -f "$TOFU_DIR/modules/$mod/outputs.tf" ]
  done
}

@test "opentofu: timescaledb module outputs service_name and port 5432" {
  grep -q "service_name" "$TOFU_DIR/modules/timescaledb/outputs.tf"
  grep -q "5432" "$TOFU_DIR/modules/timescaledb/outputs.tf"
}

@test "opentofu: minio module outputs api_port 9000 and console_port 9001" {
  grep -q "9000" "$TOFU_DIR/modules/minio/outputs.tf"
  grep -q "9001" "$TOFU_DIR/modules/minio/outputs.tf"
}

@test "opentofu: nats module outputs client_port 4222" {
  grep -q "4222" "$TOFU_DIR/modules/nats/outputs.tf"
}

@test "opentofu: oxigraph module outputs http_port 7878" {
  grep -q "7878" "$TOFU_DIR/modules/oxigraph/outputs.tf"
}

@test "opentofu: environments dir has utokyo-eng2 and gutp tfvars" {
  [ -f "$TOFU_DIR/environments/utokyo-eng2.tfvars" ]
  [ -f "$TOFU_DIR/environments/gutp.tfvars" ]
}

@test "opentofu: backend.hcl.example documents MinIO remote state" {
  grep -q "tofu-state" "$TOFU_DIR/backend.hcl.example"
  grep -q "minio" "$TOFU_DIR/backend.hcl.example"
}

# ── Helm chart structure ───────────────────────────────────────────────────────

@test "helm: all charts have Chart.yaml and values.yaml" {
  for chart in api-server connector-worker web-client gateway-bridge; do
    [ -f "$HELM_DIR/$chart/Chart.yaml" ]
    [ -f "$HELM_DIR/$chart/values.yaml" ]
  done
}

@test "helm: api-server chart has Deployment and Service templates" {
  [ -f "$HELM_DIR/api-server/templates/deployment.yaml" ]
  [ -f "$HELM_DIR/api-server/templates/service.yaml" ]
}

@test "helm: api-server deployment has prometheus scrape annotation" {
  grep -q "prometheus.io/scrape" "$HELM_DIR/api-server/templates/deployment.yaml"
}

@test "helm: connector-worker chart has Deployment template" {
  [ -f "$HELM_DIR/connector-worker/templates/deployment.yaml" ]
}

@test "helm: web-client chart has Deployment and Service templates" {
  [ -f "$HELM_DIR/web-client/templates/deployment.yaml" ]
  [ -f "$HELM_DIR/web-client/templates/service.yaml" ]
}

@test "helm: api-server chart uses keycloak env vars (not Azure AD)" {
  grep -q "KEYCLOAK" "$HELM_DIR/api-server/templates/deployment.yaml"
  ! grep -q "AZURE_TENANT_ID" "$HELM_DIR/api-server/templates/deployment.yaml"
}

@test "helm: web-client chart uses NEXT_PUBLIC_KEYCLOAK_ISSUER (not MSAL)" {
  grep -q "KEYCLOAK_ISSUER" "$HELM_DIR/web-client/templates/deployment.yaml"
}
