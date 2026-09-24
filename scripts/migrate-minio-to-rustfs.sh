#!/usr/bin/env bash
# One-time migration for an EXISTING docker-compose.oss.yaml stack (#490/#489): before this change,
# building-os.minio's data lived in a `minio_data` volume written by the real MinIO server. RustFS's
# on-disk format is not compatible with MinIO's despite both speaking the S3 API, so simply pointing
# the new rustfs/rustfs image at that same volume would silently make every previously-written object
# unreadable rather than migrate it — the new server would just see an unrecognised directory tree.
#
# This script copies the lake bucket's objects across via the S3 API instead (mc mirror), which both
# servers understand identically regardless of on-disk format. It needs your OLD building-os.minio
# container still running on the MinIO image — do NOT `docker compose pull`/recreate it before running
# this. A brand-new clone with no prior minio_data volume does not need this script at all.
#
# Stop anything still writing to the lake first (e.g. `docker compose -f docker-compose.oss.yaml stop building-os.connector-worker`)
# — this is a point-in-time copy, not live replication, and it treats a mismatched object count between
# old and new as a hard failure specifically so a concurrent write during the mirror cannot pass as a
# silent success.
#
# Usage:
#   scripts/migrate-minio-to-rustfs.sh <old_minio_container> [bucket]
#
# Example:
#   scripts/migrate-minio-to-rustfs.sh building-os.minio cold
#
# After it finishes, `docker compose -f docker-compose.oss.yaml up -d` will (re)create
# building-os.minio on rustfs/rustfs with the migrated data already in its new rustfs_data volume.
set -euo pipefail

OLD_CONTAINER="${1:?usage: $0 <old_minio_container> [bucket]}"
BUCKET="${2:-cold}"
NETWORK="building-os-oss"
# Ask Compose itself for the volume name it will actually create — reimplementing its
# project-name-prefixing convention by hand would silently drift if that convention ever changes.
NEW_VOLUME="$(docker compose -f docker-compose.oss.yaml config --format json \
  | python3 -c 'import sys,json; print(json.load(sys.stdin)["volumes"]["rustfs_data"]["name"])')"
TEMP_CONTAINER="building-os.minio-migrate-temp"

# Resolve credentials from the OLD container's *actual* running environment, not this shell's — a
# `.env`-file override (docker-compose.oss.yaml's normal mechanism) never reaches the calling shell,
# so trusting $MINIO_ROOT_USER/$MINIO_ROOT_PASSWORD here would silently fall back to the dev defaults
# and fail every `mc alias set` below on any installation with real credentials.
container_env() {
  docker exec "${OLD_CONTAINER}" printenv "$1" 2>/dev/null || true
}
ACCESS_KEY="$(container_env MINIO_ROOT_USER)"; ACCESS_KEY="${ACCESS_KEY:-${MINIO_ROOT_USER:-buildingos}}"
SECRET_KEY="$(container_env MINIO_ROOT_PASSWORD)"; SECRET_KEY="${SECRET_KEY:-${MINIO_ROOT_PASSWORD:-buildingos123}}"

echo "Old container   : ${OLD_CONTAINER}"
echo "Bucket           : ${BUCKET}"
echo "Target network   : ${NETWORK}"
echo "Target volume    : ${NEW_VOLUME} (override by pre-creating it under this name)"
echo

if ! docker inspect "${OLD_CONTAINER}" >/dev/null 2>&1; then
  echo "error: container '${OLD_CONTAINER}' not found. Is the old MinIO-backed stack running?" >&2
  exit 1
fi
if ! docker exec "${OLD_CONTAINER}" mc --version >/dev/null 2>&1; then
  echo "error: 'mc' not found inside ${OLD_CONTAINER} — is this really the old minio/minio image" \
    "(not already switched to rustfs/rustfs)?" >&2
  exit 1
fi

echo "==> Starting a temporary RustFS instance backed by the target volume (${NEW_VOLUME})..."
docker rm -f "${TEMP_CONTAINER}" >/dev/null 2>&1 || true
docker volume create "${NEW_VOLUME}" >/dev/null
docker run -d --name "${TEMP_CONTAINER}" --network "${NETWORK}" \
  -e RUSTFS_VOLUMES=/data \
  -e RUSTFS_ADDRESS=0.0.0.0:9000 \
  -e RUSTFS_ACCESS_KEY="${ACCESS_KEY}" \
  -e RUSTFS_SECRET_KEY="${SECRET_KEY}" \
  -v "${NEW_VOLUME}:/data" \
  rustfs/rustfs:1.0.0 >/dev/null

echo "==> Waiting for the temporary RustFS instance to become healthy..."
for _ in $(seq 1 30); do
  if docker exec "${TEMP_CONTAINER}" curl -fsS http://127.0.0.1:9000/health >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

echo "==> Configuring mc aliases (run from inside the old container, which already has mc)..."
docker exec "${OLD_CONTAINER}" mc alias set migrate-old "http://localhost:9000" "${ACCESS_KEY}" "${SECRET_KEY}" --quiet >/dev/null
docker exec "${OLD_CONTAINER}" mc alias set migrate-new "http://${TEMP_CONTAINER}:9000" "${ACCESS_KEY}" "${SECRET_KEY}" --quiet >/dev/null

echo "==> Creating destination bucket (mc mirror does not create it for you)..."
docker exec "${OLD_CONTAINER}" mc mb --ignore-existing "migrate-new/${BUCKET}" >/dev/null

lake_count() {
  docker exec "${OLD_CONTAINER}" mc ls --recursive "$1" 2>/dev/null | wc -l | tr -d ' '
}

# Two passes, not one: a writer still active during the first mirror can add objects after mc has
# already scanned their prefix, so a single pass can under-copy without mc reporting any error. The
# second pass catches exactly that window; if counts still disagree after it, something is still
# writing (or genuinely failed) and this must not report success.
echo "==> Mirroring migrate-old/${BUCKET} -> migrate-new/${BUCKET} (pass 1, this can take a while for a large lake)..."
docker exec "${OLD_CONTAINER}" mc mirror --overwrite "migrate-old/${BUCKET}" "migrate-new/${BUCKET}"
echo "==> Mirroring migrate-old/${BUCKET} -> migrate-new/${BUCKET} (pass 2, catches anything written during pass 1)..."
docker exec "${OLD_CONTAINER}" mc mirror --overwrite "migrate-old/${BUCKET}" "migrate-new/${BUCKET}"

OLD_COUNT="$(lake_count "migrate-old/${BUCKET}")"
NEW_COUNT="$(lake_count "migrate-new/${BUCKET}")"
echo "==> Object count — old: ${OLD_COUNT}, new: ${NEW_COUNT}"

echo "==> Cleaning up the temporary RustFS instance (data stays in ${NEW_VOLUME})..."
docker rm -f "${TEMP_CONTAINER}" >/dev/null

if [ "${OLD_COUNT}" != "${NEW_COUNT}" ]; then
  cat <<EOF >&2

FAILED: object counts still differ after two mirror passes (old: ${OLD_COUNT}, new: ${NEW_COUNT}).
This usually means something is still writing to ${OLD_CONTAINER} — stop the writer
(e.g. \`docker compose -f docker-compose.oss.yaml stop building-os.connector-worker\`) and re-run this script.
${NEW_VOLUME} was left in place for inspection but should NOT be trusted yet.
EOF
  exit 1
fi

cat <<EOF

Done. ${NEW_VOLUME} now holds a verified copy of ${OLD_CONTAINER}'s '${BUCKET}' bucket
(${NEW_COUNT} objects, matching the source).
Next: docker compose -f docker-compose.oss.yaml up -d
      (this recreates building-os.minio on rustfs/rustfs, using ${NEW_VOLUME})
Your original minio_data volume is untouched; remove it yourself once you've
confirmed the new stack reads correctly.
EOF
