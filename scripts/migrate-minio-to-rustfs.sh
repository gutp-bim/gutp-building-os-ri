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
ACCESS_KEY="${MINIO_ROOT_USER:-buildingos}"
SECRET_KEY="${MINIO_ROOT_PASSWORD:-buildingos123}"

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

echo "==> Mirroring migrate-old/${BUCKET} -> migrate-new/${BUCKET} (this can take a while for a large lake)..."
docker exec "${OLD_CONTAINER}" mc mirror --overwrite "migrate-old/${BUCKET}" "migrate-new/${BUCKET}"

OLD_COUNT="$(docker exec "${OLD_CONTAINER}" mc ls --recursive "migrate-old/${BUCKET}" 2>/dev/null | wc -l | tr -d ' ')"
NEW_COUNT="$(docker exec "${OLD_CONTAINER}" mc ls --recursive "migrate-new/${BUCKET}" 2>/dev/null | wc -l | tr -d ' ')"
echo "==> Object count — old: ${OLD_COUNT}, new: ${NEW_COUNT}"
if [ "${OLD_COUNT}" != "${NEW_COUNT}" ]; then
  echo "warning: object counts differ — inspect before trusting the migrated volume." >&2
fi

echo "==> Cleaning up the temporary RustFS instance (data stays in ${NEW_VOLUME})..."
docker rm -f "${TEMP_CONTAINER}" >/dev/null

cat <<EOF

Done. ${NEW_VOLUME} now holds a copy of ${OLD_CONTAINER}'s '${BUCKET}' bucket.
Next: docker compose -f docker-compose.oss.yaml up -d
      (this recreates building-os.minio on rustfs/rustfs, using ${NEW_VOLUME})
Your original minio_data volume is untouched; remove it yourself once you've
confirmed the new stack reads correctly.
EOF
