#!/usr/bin/env bash
# #521: every NON-mTLS ingress route must strip the trusted gateway identity header (X-Gateway-Id)
# before the request reaches the API Server. The header is only trustworthy when the mTLS ingress
# sets it from the verified client certificate (#224); a general route that forwards a client-sent
# X-Gateway-Id lets anyone read another gateway's point list.
#
# This renders the charts with `helm template` and asserts, for each rendering that exposes a
# general (non-mTLS) IngressRoute:
#   1. a Traefik headers Middleware exists that sets the trusted header to "" (Traefik deletes a
#      request header whose customRequestHeaders value is empty), and
#   2. EVERY route of that IngressRoute references the middleware — a single route without it is
#      the bypass.
#
# Usage: bash scripts/verify-helm-gateway-id-strip.sh   (requires `helm` on PATH)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UMBRELLA="${REPO_ROOT}/kubernetes/helm/building-os"
API_CHART="${REPO_ROOT}/kubernetes/helm/api-server"

if ! command -v helm >/dev/null 2>&1; then
  echo "error: helm is not installed" >&2
  exit 2
fi

failures=0
checks=0
fail() { echo "FAIL: $*" >&2; failures=$((failures + 1)); }
pass() { echo "ok:   $*"; }

# extract_doc <rendered> <kind> <name> — print the one YAML document of that kind + metadata.name.
extract_doc() {
  awk -v kind="$2" -v name="$3" '
    function flush() { if (k == kind && n == name) printf "%s", buf; buf = ""; k = ""; n = ""; inmeta = 0 }
    /^---/ { flush(); next }
    {
      buf = buf $0 "\n"
      if ($0 ~ /^kind:/) { k = $2 }
      if ($0 ~ /^metadata:/) { inmeta = 1; next }
      if (inmeta && $0 ~ /^[^ ]/) { inmeta = 0 }
      if (inmeta && $0 ~ /^  name:/ && n == "") { n = $2 }
    }
    END { flush() }
  ' <<< "$1"
}

# assert_strip <label> <rendered> <ingressroute-name> <middleware-name> <header>
assert_strip() {
  local label="$1" rendered="$2" route_name="$3" mw_name="$4" header="$5"
  checks=$((checks + 1))
  local mw route
  mw="$(extract_doc "${rendered}" Middleware "${mw_name}")"
  route="$(extract_doc "${rendered}" IngressRoute "${route_name}")"

  if [ -z "${mw}" ]; then fail "${label}: Middleware/${mw_name} not rendered"; return; fi
  if ! grep -q "customRequestHeaders:" <<< "${mw}" \
     || ! grep -Eq "^[[:space:]]+\"?${header}\"?: \"\"$" <<< "${mw}"; then
    fail "${label}: Middleware/${mw_name} does not blank ${header}"; echo "${mw}" >&2; return
  fi
  if [ -z "${route}" ]; then fail "${label}: IngressRoute/${route_name} not rendered"; return; fi

  local routes attached
  routes="$(grep -c -- '- match:' <<< "${route}" || true)"
  attached="$(grep -Ec -- "^[[:space:]]+- name: \"?${mw_name}\"?$" <<< "${route}" || true)"
  if [ "${routes}" -eq 0 ]; then fail "${label}: IngressRoute/${route_name} has no routes"; return; fi
  if [ "${attached}" -ne "${routes}" ]; then
    fail "${label}: ${attached}/${routes} routes of IngressRoute/${route_name} attach ${mw_name}"
    echo "${route}" >&2
    return
  fi
  pass "${label}: ${routes}/${routes} routes strip ${header}"
}

# assert_absent <label> <rendered> <kind> — nothing of that kind is rendered.
assert_absent() {
  checks=$((checks + 1))
  if grep -q "^kind: $3$" <<< "$2"; then fail "$1: unexpected $3 rendered"; else pass "$1: no $3"; fi
}

# ── Umbrella chart: the general IngressRoute (/api, /grpc, web-client catch-all) ──────────────────
for overlay in "" values-dev.yaml values-prod.yaml; do
  args=()
  [ -n "${overlay}" ] && args=(-f "${UMBRELLA}/${overlay}")
  out="$(helm template building-os "${UMBRELLA}" "${args[@]+"${args[@]}"}")"
  assert_strip "umbrella ${overlay:-defaults}" "${out}" building-os strip-gateway-id X-Gateway-Id
done

out="$(helm template building-os "${UMBRELLA}" --set ingress.gatewayIdHeader=X-Edge-Gw)"
assert_strip "umbrella custom header" "${out}" building-os strip-gateway-id X-Edge-Gw

out="$(helm template building-os "${UMBRELLA}" -f "${UMBRELLA}/values-minimal.yaml")"
assert_absent "umbrella minimal (ingress off)" "${out}" Middleware

# ── api-server chart: its own host (Argo CD reference: api.example.com) ────────────────────────────
out="$(helm template api-server "${API_CHART}" -f "${REPO_ROOT}/argocd/values/reference.yaml")"
assert_strip "api-server argocd reference" "${out}" api-server-api-server api-server-api-server-strip-gateway-id X-Gateway-Id

out="$(helm template api-server "${API_CHART}" --set ingress.enabled=true --set ingress.gatewayIdHeader=X-Edge-Gw)"
assert_strip "api-server custom header" "${out}" api-server-api-server api-server-api-server-strip-gateway-id X-Edge-Gw

out="$(helm template api-server "${API_CHART}")"
assert_absent "api-server defaults (ingress off)" "${out}" IngressRoute

echo "${checks} check(s), ${failures} failure(s)"
[ "${failures}" -eq 0 ]
