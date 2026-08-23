#!/bin/bash
# set -e: this script is upstream of a CI gate (#357), and without it a failed generation step falls
# through to the aspida regeneration below, which would then rebuild the client from a stale
# swagger.yaml — the same false-green that bit generate_swagger.bash (#354).
set -euo pipefail

repository_root=$(git rev-parse --show-toplevel)

# The single pinned version of the aspida generator. `.github/workflows/pr-check.yml` reads it from
# THIS line rather than repeating the literal: the CI drift check regenerates the committed client
# and fails on any difference, so two sites drifting apart would produce a red whose own error
# message ("regenerate with Tools/sync-type.bash") could not fix it.
OPENAPI2ASPIDA_VERSION="0.24.0"

# Delegate rather than duplicate: lines 7-27 of this script used to be a verbatim copy of
# generate_swagger.bash, and CI gates *that* script. A new export or DLL-path branch added there
# would have left this copy behind, so developers would regenerate a different swagger.yaml locally
# than CI does. One implementation, gated in one place.
bash "$repository_root/Tools/generate_swagger.bash"

CLIENT_PATH="$repository_root/web-client/src/lib/infra/aspida-client/generated"

if [ -d "$CLIENT_PATH" ]; then
  # ${VAR:?} so an empty CLIENT_PATH aborts instead of expanding to `rm -rf /*`
  rm -rf "${CLIENT_PATH:?}"/*
else
  mkdir -p "$CLIENT_PATH"
fi

cd "$repository_root/web-client"
npx --yes "openapi2aspida@${OPENAPI2ASPIDA_VERSION}" \
  -i="$repository_root/docs/schema/swagger.yaml" -o="$CLIENT_PATH"
