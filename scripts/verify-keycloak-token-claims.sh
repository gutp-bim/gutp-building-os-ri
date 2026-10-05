#!/usr/bin/env bash
# #582: verify against a live Keycloak that the shipped realm puts `sub` on user tokens and
# `sub` + `client_id` on the api-server client-credentials token. A static check of realm.json
# (Tools/e2e-performance/tests/test_keycloak_token_claims.py) cannot see what Keycloak actually emits.
#
#   KEYCLOAK_URL=http://localhost:8080 scripts/verify-keycloak-token-claims.sh
set -euo pipefail

KEYCLOAK_URL="${KEYCLOAK_URL:-http://localhost:8080}"
REALM="${KEYCLOAK_REALM:-building-os}"
USER_NAME="${KEYCLOAK_TEST_USER:-admin}"
USER_PASSWORD="${KEYCLOAK_TEST_PASSWORD:-admin}"
SA_SECRET="${KEYCLOAK_ADMIN_CLIENT_SECRET:-change-me-in-production}"
TOKEN_URL="$KEYCLOAK_URL/realms/$REALM/protocol/openid-connect/token"

claims() { # stdin: token response JSON; stdout: access-token claims JSON
  python3 -c '
import sys, json, base64
tok = json.load(sys.stdin).get("access_token")
if not tok:
    sys.exit("no access_token in response")
p = tok.split(".")[1]; p += "=" * (-len(p) % 4)
print(json.dumps(json.loads(base64.urlsafe_b64decode(p))))'
}

has() { # $1 claims JSON, $2 claim name -> non-empty string?
  python3 -c 'import sys,json; v=json.loads(sys.argv[1]).get(sys.argv[2]); sys.exit(0 if isinstance(v,str) and v else 1)' "$1" "$2"
}

fail=0
check() { # $1 label, $2 claims, $3 claim
  if has "$2" "$3"; then echo "PASS  $1: $3 present"; else echo "FAIL  $1: $3 missing"; fail=1; fi
}

user=$(curl -sf "$TOKEN_URL" -d grant_type=password -d client_id=web-client \
  -d "username=$USER_NAME" -d "password=$USER_PASSWORD" | claims)
check "user token" "$user" sub

sa=$(curl -sf "$TOKEN_URL" -d grant_type=client_credentials -d client_id=api-server \
  -d "client_secret=$SA_SECRET" | claims)
check "service-account token" "$sa" sub
check "service-account token" "$sa" client_id

# the user token must not look like a client-credentials token
if has "$user" client_id; then echo "FAIL  user token: client_id must be absent"; fail=1; fi
exit $fail
