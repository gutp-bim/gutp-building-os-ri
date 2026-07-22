#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

fail() {
  echo "ERROR: $*" >&2
  exit 1
}

if rg -n '@azure/msal|msal-config|msal\.token\.keys|NEXT_PUBLIC_MSAL' \
  web-client kubernetes/helm README.md CLAUDE.md \
  --glob '!**/node_modules/**' \
  --glob '!**/yarn.lock' \
  --glob '!**/README.md' >/tmp/oss-msal-scan.txt; then
  cat /tmp/oss-msal-scan.txt >&2
  fail "MSAL frontend references remain in OSS runtime files"
fi

python3 - <<'PY'
import json
from pathlib import Path

realm_path = Path("oss-stack/keycloak/realm.json")
if not realm_path.exists():
    raise SystemExit("missing oss-stack/keycloak/realm.json")

realm = json.loads(realm_path.read_text(encoding="utf-8"))
if realm.get("realm") != "building-os":
    raise SystemExit("realm must be building-os")

clients = {client.get("clientId"): client for client in realm.get("clients", [])}
required_clients = {"web-client", "api-server"}
missing = required_clients - clients.keys()
if missing:
    raise SystemExit(f"missing Keycloak clients: {sorted(missing)}")

for client_id in ("web-client",):
    client = clients[client_id]
    if client.get("publicClient") is not True:
        raise SystemExit(f"{client_id} must be a public client")

api_client = clients["api-server"]
if api_client.get("publicClient") is True:
    raise SystemExit("api-server must be confidential")
if api_client.get("serviceAccountsEnabled") is not True:
    raise SystemExit("api-server service account must be enabled")

roles = {role.get("name") for role in realm.get("roles", {}).get("realm", [])}
for role in ("building-os-admin", "building-os-operator", "building-os-viewer"):
    if role not in roles:
        raise SystemExit(f"missing realm role: {role}")
PY

python3 - <<'PY'
from pathlib import Path

required = {
    "docs/operations/keycloak-permission-mapping.md": [
        "Azure AD to Keycloak Permission Mapping",
        "Service Accounts",
        "HITL Sign-Off",
    ],
    "docs/operations/keycloak-admin-provisioning.md": [
        "Keycloak Admin Provisioning",
        "Realm Import",
        "Role Synchronization",
    ],
    "docs/operations/nextjs-k8s-rollout.md": [
        "Next.js Kubernetes Rollout",
        "Rollback",
        "SWA Parity",
    ],
    "docs/architecture/oss-nats-design.md": [
        "NATS JetStream Design",
        "Subject Contract",
        "HITL Sign-Off",
    ],
    "docs/architecture/oss-hono-design.md": [
        "OSS Device Connectivity Design",
        "Provisioning",
        "HITL Sign-Off",
    ],
    "docs/project/hono-device-test-plan.md": [
        "Hono Device Test Plan",
        "Development Edge Device",
        "Rollback",
    ],
}

for filename, headings in required.items():
    path = Path(filename)
    if not path.exists():
        raise SystemExit(f"missing {filename}")
    text = path.read_text(encoding="utf-8")
    text_lower = text.lower()
    for heading in headings:
        if heading.lower() not in text_lower:
            raise SystemExit(f"{filename} missing heading/text: {heading}")
PY

# Check for :latest tags in third-party infra images in docker-compose.oss.yaml.
# Non-blocking: logs unpinned images so maintainers can track and pin them.
python3 - <<'PY'
import re
from pathlib import Path

compose = Path("docker-compose.oss.yaml").read_text(encoding="utf-8")

# First-party images are built from this repo — :latest is managed by CI/CD.
first_party_prefixes = ("buildingos/",)

unpinned = []
for line in compose.splitlines():
    m = re.match(r'\s+image:\s+(\S+)', line)
    if m:
        image = m.group(1)
        if image.endswith(":latest") and not any(image.startswith(p) for p in first_party_prefixes):
            unpinned.append(image)

if unpinned:
    print("WARNING: third-party images using :latest (pin to a specific version for reproducibility):")
    for img in unpinned:
        print(f"  {img}")
PY

echo "OSS issue readiness checks passed"
