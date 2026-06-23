#!/bin/bash
# Build and push Building OS Docker images.
#
# Environment variables:
#   REGISTRY        Primary registry to push to (default: ACR)
#   HARBOR_REGISTRY Optional second registry for dual push (e.g. harbor.example.com)
#   IMAGE_TAG       Image tag (default: latest)
#   IMAGES          Space-separated list of images to build (default: api-server)
#
# Examples:
#   # Legacy ACR push (original behaviour)
#   ./build-and-push-api-server.bash
#
#   # Push to Harbor only
#   REGISTRY=harbor.example.com/buildingos ./build-and-push-api-server.bash
#
#   # Dual push: ACR + Harbor
#   HARBOR_REGISTRY=harbor.example.com/buildingos ./build-and-push-api-server.bash
#
#   # Push all images
#   IMAGES="api-server connector-worker web-client" ./build-and-push-api-server.bash

set -euo pipefail

repository_root=$(git rev-parse --show-toplevel)
cd "$repository_root"

ACR_REGISTRY="${ACR_REGISTRY:-utokyobuildingoseng2containerregistry.azurecr.io}"
REGISTRY="${REGISTRY:-$ACR_REGISTRY}"
HARBOR_REGISTRY="${HARBOR_REGISTRY:-}"
IMAGE_TAG="${IMAGE_TAG:-latest}"
IMAGES="${IMAGES:-api-server}"

declare -A IMAGE_CONTEXT=(
  [api-server]="DotNet"
  [connector-worker]="DotNet"
  [web-client]="web-client"
)

declare -A IMAGE_DOCKERFILE=(
  [api-server]="DotNet/BuildingOS.ApiServer/Dockerfile"
  [connector-worker]="DotNet/BuildingOS.ConnectorWorker/Dockerfile"
  [web-client]="web-client/Dockerfile"
)

declare -A IMAGE_NAME=(
  [api-server]="gutp-building-os-api-server"
  [connector-worker]="gutp-building-os-connector-worker"
  [web-client]="gutp-building-os-web-client"
)

# Login
if [[ "$REGISTRY" == *".azurecr.io"* ]]; then
  echo "[build] Logging in to Azure CLI + ACR..."
  az login
  az acr login --name "${REGISTRY%%.*}"
elif [[ -n "$REGISTRY" ]]; then
  echo "[build] REGISTRY=$REGISTRY — ensure docker login is already done or set HARBOR_ROBOT_NAME/HARBOR_ROBOT_SECRET"
  if [[ -n "${HARBOR_ROBOT_NAME:-}" && -n "${HARBOR_ROBOT_SECRET:-}" ]]; then
    echo "$HARBOR_ROBOT_SECRET" | docker login "$REGISTRY" -u "$HARBOR_ROBOT_NAME" --password-stdin
  fi
fi

for IMAGE in $IMAGES; do
  CONTEXT="${IMAGE_CONTEXT[$IMAGE]}"
  DOCKERFILE="${IMAGE_DOCKERFILE[$IMAGE]}"
  NAME="${IMAGE_NAME[$IMAGE]}"
  FULL_TAG="${REGISTRY}/${NAME}:${IMAGE_TAG}"

  echo "[build] Building $IMAGE → $FULL_TAG"
  docker build "$CONTEXT" -t "$FULL_TAG" -f "$DOCKERFILE"
  docker push "$FULL_TAG"
  echo "[build] Pushed $FULL_TAG"

  # Dual push to Harbor
  if [[ -n "$HARBOR_REGISTRY" ]]; then
    HARBOR_TAG="${HARBOR_REGISTRY}/${IMAGE}:${IMAGE_TAG}"
    echo "[build] Dual push: retagging $FULL_TAG → $HARBOR_TAG"
    docker tag "$FULL_TAG" "$HARBOR_TAG"
    if [[ -n "${HARBOR_ROBOT_NAME:-}" && -n "${HARBOR_ROBOT_SECRET:-}" ]]; then
      echo "$HARBOR_ROBOT_SECRET" | docker login "${HARBOR_REGISTRY%%/*}" -u "$HARBOR_ROBOT_NAME" --password-stdin
    fi
    docker push "$HARBOR_TAG"
    echo "[build] Pushed $HARBOR_TAG"
  fi
done

echo "[build] Done."
