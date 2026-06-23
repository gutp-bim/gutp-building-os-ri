#!/usr/bin/env bats
# Tests for build-and-push-api-server.bash
# Requires: bats-core (https://bats-core.readthedocs.io/)
# Run: bats Tools/tests/test_build_script.bats

SCRIPT="$(git rev-parse --show-toplevel)/Tools/build-and-push-api-server.bash"

setup() {
  # Create a temp dir to capture docker mock calls
  export BATS_TMPDIR="${BATS_TMPDIR:-/tmp}/bats-$$"
  mkdir -p "$BATS_TMPDIR"

  # Mock docker: records calls to a log file
  export DOCKER_LOG="$BATS_TMPDIR/docker.log"
  export PATH="$BATS_TMPDIR/bin:$PATH"

  mkdir -p "$BATS_TMPDIR/bin"
  cat > "$BATS_TMPDIR/bin/docker" <<'EOF'
#!/bin/bash
echo "$@" >> "$DOCKER_LOG"
# Simulate successful login/build/push/tag
exit 0
EOF
  chmod +x "$BATS_TMPDIR/bin/docker"

  # Mock az: always succeeds silently
  cat > "$BATS_TMPDIR/bin/az" <<'EOF'
#!/bin/bash
exit 0
EOF
  chmod +x "$BATS_TMPDIR/bin/az"
}

teardown() {
  rm -rf "$BATS_TMPDIR"
}

@test "script exits 0 with ACR registry" {
  REGISTRY="myacr.azurecr.io" \
  IMAGE_TAG="test" \
  IMAGES="api-server" \
  bash "$SCRIPT"
  [ "$?" -eq 0 ]
}

@test "script pushes to custom REGISTRY" {
  REGISTRY="harbor.example.com/buildingos" \
  IMAGE_TAG="dev" \
  IMAGES="api-server" \
  bash "$SCRIPT"
  grep -q "harbor.example.com/buildingos" "$DOCKER_LOG"
}

@test "script performs dual push when HARBOR_REGISTRY is set" {
  ACR_REGISTRY="myacr.azurecr.io" \
  REGISTRY="myacr.azurecr.io" \
  HARBOR_REGISTRY="harbor.example.com/buildingos" \
  IMAGE_TAG="dual" \
  IMAGES="api-server" \
  bash "$SCRIPT"
  grep -q "myacr.azurecr.io" "$DOCKER_LOG"
  grep -q "harbor.example.com/buildingos" "$DOCKER_LOG"
}

@test "docker tag is called for Harbor dual push" {
  ACR_REGISTRY="myacr.azurecr.io" \
  REGISTRY="myacr.azurecr.io" \
  HARBOR_REGISTRY="harbor.example.com/buildingos" \
  IMAGE_TAG="v1.2.3" \
  IMAGES="api-server" \
  bash "$SCRIPT"
  grep -q "tag" "$DOCKER_LOG"
}

@test "IMAGE_TAG defaults to latest" {
  REGISTRY="myacr.azurecr.io" \
  IMAGES="api-server" \
  bash "$SCRIPT"
  grep -q ":latest" "$DOCKER_LOG"
}

@test "multiple IMAGES are all built" {
  REGISTRY="r.example.com" \
  IMAGE_TAG="multi" \
  IMAGES="api-server connector-worker" \
  bash "$SCRIPT"
  grep -q "api-server" "$DOCKER_LOG"
  grep -q "connector-worker" "$DOCKER_LOG"
}
