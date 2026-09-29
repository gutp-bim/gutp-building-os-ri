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
