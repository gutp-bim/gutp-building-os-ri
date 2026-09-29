#!/usr/bin/env bats
# Tests for Tools/check-openapi-breaking.bash (#507, ADR-0008 §3): v1 is additive-only, so a breaking
# OpenAPI change must be caught before it merges.
# Run: npx --yes bats@1.11.1 Tools/tests/test_check_openapi_breaking.bats   (Docker required for oasdiff)

SCRIPT="$(git rev-parse --show-toplevel)/Tools/check-openapi-breaking.bash"

setup() {
  WORK="$(mktemp -d "${BATS_TMPDIR:-/tmp}/oasdiff.XXXXXX")"
  cat > "$WORK/base.yaml" <<'YAML'
openapi: 3.0.1
info: { title: t, version: v1 }
paths:
  /api/v1/points/{pointId}:
    get:
      parameters:
        - { name: pointId, in: path, required: true, schema: { type: string } }
      responses:
        '200':
          description: ok
          content:
            application/json:
              schema:
                type: object
                required: [id, name]
                properties:
                  id: { type: string }
                  name: { type: string }
YAML
}

teardown() { rm -rf "$WORK"; }

@test "identical specs pass" {
  cp "$WORK/base.yaml" "$WORK/head.yaml"
  run bash "$SCRIPT" --base-file "$WORK/base.yaml" --head-file "$WORK/head.yaml"
  [ "$status" -eq 0 ]
  [[ "$output" == *"No breaking changes"* ]]
}

@test "an additive change (new optional field, new endpoint) passes" {
  awk '{ print } /                  name: \{ type: string \}/ { print "                  unit: { type: string }" }' \
    "$WORK/base.yaml" > "$WORK/head.yaml"
  grep -q "unit: { type: string }" "$WORK/head.yaml" # the field really was added
  cat >> "$WORK/head.yaml" <<'YAML'
  /api/v1/points:
    get:
      responses:
        '200': { description: ok }
YAML
  run bash "$SCRIPT" --base-file "$WORK/base.yaml" --head-file "$WORK/head.yaml"
  [ "$status" -eq 0 ]
}

@test "removing a response field fails and names the endpoint" {
  grep -v "name: { type: string }" "$WORK/base.yaml" | sed 's/required: \[id, name\]/required: [id]/' > "$WORK/head.yaml"
  run bash "$SCRIPT" --base-file "$WORK/base.yaml" --head-file "$WORK/head.yaml"
  [ "$status" -ne 0 ]
  [[ "$output" == *"/api/v1/points/{pointId}"* ]]
}

@test "removing an endpoint fails" {
  printf 'openapi: 3.0.1\ninfo: { title: t, version: v1 }\npaths: {}\n' > "$WORK/head.yaml"
  run bash "$SCRIPT" --base-file "$WORK/base.yaml" --head-file "$WORK/head.yaml"
  [ "$status" -ne 0 ]
}

@test "the default mode compares docs/schema/swagger.yaml with the base ref" {
  run bash "$SCRIPT" HEAD
  [ "$status" -eq 0 ]
}

@test "the git mode compares with the merge-base, not the base branch tip" {
  # An endpoint added on main after the branch split must not read as "removed" by the branch.
  repo="$WORK/repo"; mkdir -p "$repo/docs/schema" "$repo/Tools"
  cp "$SCRIPT" "$repo/Tools/"
  cp "$WORK/base.yaml" "$repo/docs/schema/swagger.yaml"
  git -C "$repo" init -q -b main
  git -C "$repo" -c user.email=t@t -c user.name=t add -A
  git -C "$repo" -c user.email=t@t -c user.name=t commit -qm base
  git -C "$repo" checkout -qb feature
  git -C "$repo" checkout -q main
  cat >> "$repo/docs/schema/swagger.yaml" <<'YAML'
  /api/v1/later:
    get:
      responses:
        '200': { description: ok }
YAML
  git -C "$repo" -c user.email=t@t -c user.name=t commit -qam "main adds an endpoint"
  git -C "$repo" checkout -q feature

  run bash -c "cd '$repo' && bash Tools/check-openapi-breaking.bash main"
  [ "$status" -eq 0 ]
}

@test "a tool failure is reported as such, not as a breaking change" {
  # No oasdiff and a docker that cannot run.
  mkdir -p "$WORK/bin"
  printf '#!/usr/bin/env bash\necho "Cannot connect to the Docker daemon" >&2\nexit 125\n' > "$WORK/bin/docker"
  chmod +x "$WORK/bin/docker"
  cp "$WORK/base.yaml" "$WORK/head.yaml"
  run env PATH="$WORK/bin:/usr/bin:/bin" bash "$SCRIPT" --base-file "$WORK/base.yaml" --head-file "$WORK/head.yaml"
  [ "$status" -eq 125 ]
  [[ "$output" == *"could not run oasdiff"* ]]
  [[ "$output" != *"Breaking change"* ]]
}
