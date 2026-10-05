"""
Keycloak token-claim wiring tests (#582).

Keycloak 25+ emits `sub` from the `basic` client scope's mapper, and `client_id` (the marker
`AuthorizationClaimResolver.IsServiceAccountToken` keys on) from the `service_account` scope. A realm that
defines `clientScopes` explicitly replaces Keycloak's defaults, so both scopes must be declared here or the
tokens lose those claims: control-audit `actorSub` becomes "unknown" (#461) and a group-manager service
account (#506) is never recognised.

Static pin of the shipped realm; the live-token check is
`scripts/verify-keycloak-token-claims.sh`.

Run:
    cd Tools/e2e-performance && python -m pytest tests/test_keycloak_token_claims.py -v
"""
import json
from pathlib import Path

REALM_JSON = Path(__file__).parent.parent.parent.parent / "oss-stack" / "keycloak" / "realm.json"


def load_realm():
    return json.loads(REALM_JSON.read_text())


def scope(realm, name):
    matches = [s for s in realm["clientScopes"] if s["name"] == name]
    assert len(matches) == 1, f"realm.json must define the {name!r} client scope exactly once"
    return matches[0]


def mappers(s):
    return {m["protocolMapper"]: m for m in s.get("protocolMappers", [])}


def client(realm, client_id):
    return next(c for c in realm["clients"] if c["clientId"] == client_id)


def test_basic_scope_maps_sub_into_the_access_token():
    m = mappers(scope(load_realm(), "basic"))
    assert "oidc-sub-mapper" in m
    assert m["oidc-sub-mapper"]["config"]["access.token.claim"] == "true"


def test_user_facing_clients_request_basic_by_default():
    realm = load_realm()
    for client_id in ("web-client", "api-server"):
        assert "basic" in client(realm, client_id)["defaultClientScopes"], client_id


def test_service_account_scope_puts_client_id_on_the_token():
    realm = load_realm()
    m = mappers(scope(realm, "service_account"))
    mapper = next(
        (x for x in m.values() if x["config"].get("claim.name") == "client_id"), None
    )
    assert mapper is not None, "service_account scope must emit the client_id claim"
    assert mapper["config"]["access.token.claim"] == "true"
    assert "service_account" in client(realm, "api-server")["defaultClientScopes"]
    # a user-facing public client must not carry it: client_id marks a client-credentials token
    assert "service_account" not in client(realm, "web-client")["defaultClientScopes"]
