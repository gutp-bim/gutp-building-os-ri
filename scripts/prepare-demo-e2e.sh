#!/usr/bin/env bash
set -euo pipefail

readonly TIMEOUT_SECONDS="${DEMO_E2E_READY_TIMEOUT_SEC:-180}"
readonly KEYCLOAK_BASE_URL="${DEMO_E2E_KEYCLOAK_URL:-http://localhost:8080}"
readonly KEYCLOAK_HEALTH_URL="${DEMO_E2E_KEYCLOAK_HEALTH_URL:-http://localhost:8180/health/ready}"
readonly REALM_FILE="${DEMO_E2E_REALM_FILE:-oss-stack/keycloak/realm.json}"
readonly -a COMPOSE=(
  docker compose
  -f docker-compose.oss.yaml
  -f docker-compose.demo.yaml
  -f docker-compose.demo-e2e.yaml
  --profile demo
  --profile webclient
  --profile demo-e2e
)

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required command not found: $1" >&2
    exit 1
  fi
}

wait_for_http() {
  local name="$1"
  local url="$2"
  local deadline=$((SECONDS + TIMEOUT_SECONDS))

  until curl -fsS --connect-timeout 2 --max-time 5 "$url" >/dev/null 2>&1; do
    if (( SECONDS >= deadline )); then
      echo "Timed out waiting for ${name}: ${url}" >&2
      exit 1
    fi
    sleep 2
  done
  echo "Ready: ${name}"
}

wait_for_command() {
  local name="$1"
  shift
  local deadline=$((SECONDS + TIMEOUT_SECONDS))

  until "$@" >/dev/null 2>&1; do
    if (( SECONDS >= deadline )); then
      echo "Timed out waiting for ${name}" >&2
      exit 1
    fi
    sleep 2
  done
  echo "Ready: ${name}"
}

configure_keycloak_client() {
  local admin_user="${KC_BOOTSTRAP_ADMIN_USERNAME:-admin}"
  local admin_password="${KC_BOOTSTRAP_ADMIN_PASSWORD:-admin}"
  local token
  local client_id
  local client
  local redirect_uris
  local web_origins
  local updated_client

  token="$(curl -fsS --connect-timeout 5 --max-time 15 \
    --data-urlencode 'client_id=admin-cli' \
    --data-urlencode "username=${admin_user}" \
    --data-urlencode "password=${admin_password}" \
    --data-urlencode 'grant_type=password' \
    "${KEYCLOAK_BASE_URL}/realms/master/protocol/openid-connect/token" \
    | jq -er '.access_token')"

  client_id="$(curl -fsS --connect-timeout 5 --max-time 15 \
    -H "Authorization: Bearer ${token}" \
    "${KEYCLOAK_BASE_URL}/admin/realms/building-os/clients?clientId=web-client" \
    | jq -er 'if length == 1 then .[0].id else error("web-client not found") end')"

  client="$(curl -fsS --connect-timeout 5 --max-time 15 \
    -H "Authorization: Bearer ${token}" \
    "${KEYCLOAK_BASE_URL}/admin/realms/building-os/clients/${client_id}")"
  redirect_uris="$(jq -cer '.clients[] | select(.clientId == "web-client") | .redirectUris' "$REALM_FILE")"
  web_origins="$(jq -cer '.clients[] | select(.clientId == "web-client") | .webOrigins' "$REALM_FILE")"
  updated_client="$(jq -cer \
    --argjson redirectUris "$redirect_uris" \
    --argjson webOrigins "$web_origins" \
    '.redirectUris = $redirectUris | .webOrigins = $webOrigins' \
    <<<"$client")"

  curl -fsS --connect-timeout 5 --max-time 15 -X PUT \
    -H "Authorization: Bearer ${token}" \
    -H 'Content-Type: application/json' \
    --data "$updated_client" \
    "${KEYCLOAK_BASE_URL}/admin/realms/building-os/clients/${client_id}"
  echo "Ready: Keycloak web-client redirects"
}

require_command curl
require_command docker
require_command jq

if [[ ! -f "$REALM_FILE" ]]; then
  echo "Keycloak realm file not found: $REALM_FILE" >&2
  exit 1
fi

wait_for_http "Keycloak" "$KEYCLOAK_HEALTH_URL"
wait_for_command "API" \
  "${COMPOSE[@]}" exec -T building-os.web node -e \
  "fetch('${DEMO_E2E_API_HEALTH_URL:-http://building-os.api:8080/health}').then(r => process.exit(r.ok ? 0 : 1)).catch(() => process.exit(1))"
wait_for_http "ConnectorWorker" "${DEMO_E2E_CONNECTOR_HEALTH_URL:-http://localhost:8081/health/ready}"
wait_for_http "Web client" "${DEMO_E2E_WEB_URL:-http://localhost:3000/sign-in}"
configure_keycloak_client
