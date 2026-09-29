#!/usr/bin/env bash
# Detect breaking changes to the REST API's OpenAPI document (#507, ADR-0008 §3).
#
# v1 is additive-only: removing or renaming a field, path or parameter, changing a type, making a
# request field required or narrowing accepted values must ship as a new version (/api/v2) instead.
# This compares docs/schema/swagger.yaml with the same file at a base ref (default origin/main) using
# oasdiff and fails on any ERR-level breaking change.
#
# Usage:
#   Tools/check-openapi-breaking.bash [BASE_REF]                       # default BASE_REF=origin/main
#   Tools/check-openapi-breaking.bash --base-file A.yaml --head-file B.yaml
#
# Regenerate the spec first (Tools/sync-type.bash) so it reflects the source. Uses a local `oasdiff`
# when on PATH, otherwise the pinned Docker image below.
set -euo pipefail

OASDIFF_IMAGE="tufin/oasdiff@sha256:0286f138545a39010525df6c1bea67ffafacb384ef800effffa63bbd04718ce5"
SPEC="docs/schema/swagger.yaml"

repository_root=$(git rev-parse --show-toplevel)
base_file="" head_file="" base_ref="origin/main"
while [ $# -gt 0 ]; do
  case "$1" in
    --base-file) base_file="$2"; shift 2 ;;
    --head-file) head_file="$2"; shift 2 ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) base_ref="$1"; shift ;;
  esac
done

work=$(mktemp -d "${TMPDIR:-/tmp}/oasdiff.XXXXXX")
trap 'rm -rf "$work"' EXIT

if [ -n "$base_file" ]; then
  cp "$base_file" "$work/base.yaml"
else
  if ! git -C "$repository_root" show "$base_ref:$SPEC" > "$work/base.yaml" 2>/dev/null; then
    echo "error: cannot read $SPEC at '$base_ref' (fetch it first, e.g. git fetch origin main)" >&2
    exit 2
  fi
fi
cp "${head_file:-$repository_root/$SPEC}" "$work/head.yaml"

run_oasdiff() {
  if command -v oasdiff >/dev/null 2>&1; then
    (cd "$work" && oasdiff "$@")
  else
    docker run --rm -v "$work:/specs:ro" -w /specs "$OASDIFF_IMAGE" "$@"
  fi
}

if [ -n "$base_file" ]; then label="$base_file"; else label="$base_ref"; fi
echo "Comparing ${head_file:-$SPEC} against $label"
if run_oasdiff breaking base.yaml head.yaml --fail-on ERR --format text; then
  echo "No breaking changes."
else
  status=$?
  echo >&2
  echo "Breaking change(s) to the v1 API (see above). Per ADR-0008 v1 is additive-only: ship the change" >&2
  echo "as /api/v2 alongside v1, or make it additive (optional field, new endpoint)." >&2
  exit "$status"
fi
