"""
Keycloak admin-client wiring tests (#532 follow-up).

The API server's user management (KeycloakUserManagementService) calls the Keycloak Admin REST API with a
client_credentials token from the confidential `api-server` client. These tests pin the shipped wiring:

- realm.json grants the api-server service account exactly the least-privilege realm-management roles the
  code needs (view-users / query-groups / manage-users) — no broader role such as realm-admin.
- docker-compose.oss.yaml points the API server at that client (KEYCLOAK_ADMIN_CLIENT_ID=api-server) with an
  overridable dev secret that matches the realm's client secret.
- the Helm chart sets the (non-secret) client id and keeps the secret as a Secret reference.

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_keycloak_admin_client_config.py -v
"""
import json
import re
from pathlib import Path

import yaml

REPO_ROOT = Path(__file__).parent.parent.parent.parent
REALM_JSON = REPO_ROOT / "oss-stack" / "keycloak" / "realm.json"
DOCKER_COMPOSE = REPO_ROOT / "docker-compose.oss.yaml"
HELM_VALUES = REPO_ROOT / "kubernetes" / "helm" / "building-os" / "values.yaml"

ADMIN_CLIENT_ID = "api-server"
EXPECTED_ROLES = {"view-users", "query-groups", "manage-users"}


def load_realm():
    return json.loads(REALM_JSON.read_text())


def api_server_client(realm):
    return next(c for c in realm["clients"] if c["clientId"] == ADMIN_CLIENT_ID)


def service_account_user(realm):
    users = [u for u in realm.get("users", []) if u.get("serviceAccountClientId") == ADMIN_CLIENT_ID]
    assert len(users) == 1, "realm.json must declare exactly one service-account user for api-server"
    return users[0]


def api_server_env():
    compose = yaml.safe_load(DOCKER_COMPOSE.read_text())
    return compose["services"]["building-os.api"]["environment"]


def default_of(value):
    """Resolve a compose `${VAR:-default}` expression to its default."""
    m = re.fullmatch(r"\$\{(\w+):-([^}]*)\}", str(value))
    assert m, f"expected an overridable ${{VAR:-default}} value, got {value!r}"
    return m.group(1), m.group(2)


def test_api_server_client_is_confidential_with_service_account():
    client = api_server_client(load_realm())
    assert client["publicClient"] is False
    assert client["serviceAccountsEnabled"] is True


def test_service_account_user_follows_keycloak_import_format():
    user = service_account_user(load_realm())
    assert user["username"] == f"service-account-{ADMIN_CLIENT_ID}"
    assert user["enabled"] is True


def test_service_account_has_exactly_least_privilege_realm_management_roles():
    user = service_account_user(load_realm())
    client_roles = user.get("clientRoles", {})
    assert set(client_roles) == {"realm-management"}, "only realm-management roles may be granted"
    assert set(client_roles["realm-management"]) == EXPECTED_ROLES
    assert not user.get("realmRoles"), "the service account must not carry realm roles"


def test_compose_api_server_uses_api_server_as_admin_client():
    env = api_server_env()
    var, default = default_of(env["KEYCLOAK_ADMIN_CLIENT_ID"])
    assert var == "KEYCLOAK_ADMIN_CLIENT_ID"
    assert default == ADMIN_CLIENT_ID


def test_compose_admin_secret_defaults_to_realm_client_secret():
    env = api_server_env()
    var, default = default_of(env["KEYCLOAK_ADMIN_CLIENT_SECRET"])
    assert var == "KEYCLOAK_ADMIN_CLIENT_SECRET"
    assert default == api_server_client(load_realm())["secret"]


def test_helm_sets_admin_client_id_and_keeps_secret_as_secret_ref():
    api = yaml.safe_load(HELM_VALUES.read_text())["apiServer"]
    assert api["env"]["KEYCLOAK_ADMIN_CLIENT_ID"] == ADMIN_CLIENT_ID
    assert "KEYCLOAK_ADMIN_CLIENT_ID" not in api["secretEnv"], "the client id is not a secret"
    # secretEnv keys are rendered as secretKeyRef (building-os-api-secrets); the value is never inlined.
    assert "KEYCLOAK_ADMIN_CLIENT_SECRET" in api["secretEnv"]
    assert api["secretEnv"]["KEYCLOAK_ADMIN_CLIENT_SECRET"] == ""
