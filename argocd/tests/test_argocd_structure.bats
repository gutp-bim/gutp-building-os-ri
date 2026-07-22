#!/usr/bin/env bats
# Structural tests for Argo CD GitOps configuration.
# Run with: bats argocd/tests/test_argocd_structure.bats

REPO_ROOT="$(cd "$(dirname "$BATS_TEST_FILENAME")/../.." && pwd)"
ARGOCD_DIR="$REPO_ROOT/argocd"

# ── Argo CD Application definitions ──────────────────────────────────────────

@test "argocd: apps/ dir contains utokyo-eng2 Application manifest" {
  [ -f "$ARGOCD_DIR/apps/utokyo-eng2.yaml" ]
}

@test "argocd: utokyo-eng2 Application kind is Application" {
  grep -q "kind: Application" "$ARGOCD_DIR/apps/utokyo-eng2.yaml"
}

@test "argocd: utokyo-eng2 Application targets building-os namespace" {
  grep -q "namespace: building-os" "$ARGOCD_DIR/apps/utokyo-eng2.yaml"
}

@test "argocd: utokyo-eng2 Application uses syncPolicy automated" {
  grep -q "automated:" "$ARGOCD_DIR/apps/utokyo-eng2.yaml"
}

@test "argocd: utokyo-eng2 Application has prune and selfHeal enabled" {
  grep -q "prune: true" "$ARGOCD_DIR/apps/utokyo-eng2.yaml"
  grep -q "selfHeal: true" "$ARGOCD_DIR/apps/utokyo-eng2.yaml"
}

@test "argocd: values/ dir contains utokyo-eng2.yaml" {
  [ -f "$ARGOCD_DIR/values/utokyo-eng2.yaml" ]
}

@test "argocd: values/utokyo-eng2.yaml has image registry config" {
  grep -q "repository" "$ARGOCD_DIR/values/utokyo-eng2.yaml"
}

# ── Argo CD install manifest ──────────────────────────────────────────────────

@test "argocd: install/ contains argocd Helm values" {
  [ -f "$ARGOCD_DIR/install/argocd-values.yaml" ]
}

@test "argocd: argocd-values.yaml configures Keycloak SSO (OIDC)" {
  grep -qi "oidc\|keycloak" "$ARGOCD_DIR/install/argocd-values.yaml"
}

@test "argocd: argocd-values.yaml enables server insecure mode or TLS" {
  grep -q "server.insecure\|server.tls" "$ARGOCD_DIR/install/argocd-values.yaml"
}

# ── Rollback documentation ────────────────────────────────────────────────────

@test "argocd: docs/operations/argocd-gitops-guide.md exists" {
  [ -f "$REPO_ROOT/docs/operations/argocd-gitops-guide.md" ]
}

@test "argocd: guide documents rollback procedure via git revert" {
  grep -qi "git revert\|rollback" "$REPO_ROOT/docs/operations/argocd-gitops-guide.md"
}

@test "argocd: guide documents multi-env expansion path (ApplicationSet)" {
  grep -qi "ApplicationSet\|multi.env\|multi-env" "$REPO_ROOT/docs/operations/argocd-gitops-guide.md"
}

# ── GitHub Actions integration ────────────────────────────────────────────────

@test "argocd: .github/workflows contains argocd image-update workflow" {
  [ -f "$REPO_ROOT/.github/workflows/argocd-image-update.yml" ]
}

@test "argocd: image-update workflow updates values file on push to main" {
  grep -q "push" "$REPO_ROOT/.github/workflows/argocd-image-update.yml"
  grep -q "values" "$REPO_ROOT/.github/workflows/argocd-image-update.yml"
}
