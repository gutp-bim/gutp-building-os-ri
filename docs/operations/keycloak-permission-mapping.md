# Azure AD to Keycloak Permission Mapping

This document defines the OSS identity model for Building OS. It is the
replacement for the former Azure AD app registration, scope, role, and managed
identity design.

## Realm and Clients

Realm: `building-os`

| Client | Type | Purpose |
|---|---|---|
| `web-client` | Public OIDC client | Main Next.js dashboard **and** the `/admin` workspace (user/group/permission management) — there is no separate admin client; the former `admin-console` app was folded into the web-client `(admin)` workspace |
| `api-server` | Confidential client | REST/gRPC API audience and service account |

The local realm import lives at `oss-stack/keycloak/realm.json` and is imported
by `docker-compose.oss.yaml` with `start-dev --import-realm`.

## Permission Model

Building OS authorization continues to use the existing permission string shape:

```text
{resourceType}:{resourceId}:{actions}
```

Examples:

| Role (`building_os_role`) | Keycloak realm role | Permission attributes |
|---|---|---|
| `admin` | `building-os-admin` | none needed — admin is decided by the role and bypasses permission checks |
| `operator` | `building-os-operator` | per-resource grants, e.g. `building:<hash>:read`, `point:<hash>:read,write` |
| `viewer` | `building-os-viewer` | per-resource grants, e.g. `building:<hash>:read`, `group:tenant-a:read` |

Type and id match **exactly**. `*` is not a wildcard: an entry such as `building:*:read` or `*:*:*`
grants nothing, and the API server logs a warning and ignores it (#505). The `operator` / `viewer`
role only selects the UI workspace; it grants no data access by itself. The realm ships only the
`admin` user and the `building-os-admins` group; add operators and viewers with explicit grants.

Resource IDs that are not group IDs remain hashed by the API authorization
layer. Keycloak stores permission strings as user or group attributes and emits
them in the `permissions` access-token claim through the `building-os-api`
client scope.

### Token claims → AuthorizationContext

The `building-os-api` client scope emits two access-token claims, read directly by
`AuthorizationContextMiddleware` (via the pure `AuthorizationClaimResolver`):

| Claim | Source attribute | AuthorizationContext field |
|---|---|---|
| `building_os_role` (single) | user attr `role` | `Role` |
| `permissions` (multivalued) | user attr `permissions` ∪ every group's `permissions` | `Permissions` |
| `idtyp=app` (client credentials) | — | `Role=admin` (service account) |

The middleware reads these **Keycloak-native** names first and falls back to the legacy
Azure-AD optional-claim names (`extension_BuildingOS_role` / `extension_BuildingOS_permissions`,
still emitted by `TestAuthenticationHandler`) for backward compatibility. Because the role/permissions
travel in the token, the common path needs **no per-request Keycloak Admin API call**; the Admin API
(`KeycloakUserManagementService`) is a fallback only (for tokens without the claims) and its result is
cached for 5 minutes. _(#10 sign-off fix, 2026-06-14: previously the middleware read only the Azure-AD
names, so real Keycloak tokens missed the claim path and every request hit the Admin API.)_

### Multiple groups: `permissions` is a union

The `building-os-permissions` mapper sets `aggregate.attrs=true`, so the `permissions` claim is the
**union of the user's own attribute and the attribute of every group the user belongs to**. A user in
both a tenant-A group and a tenant-B group gets both groups' permission strings. Without it Keycloak
uses the user attribute alone, or — when the user has none — only the **first** group it finds, which
makes group-composed permissions silently drop all but one group (#508).

`building_os_role` stays single-valued; it is not aggregated.

A realm imported before this setting keeps the old mapper config (`--import-realm` skips an existing
realm). Apply it to a running Keycloak with:

```bash
kcadm.sh config credentials --server "$KC_URL" --realm master --user "$KC_ADMIN" --password "$KC_ADMIN_PASSWORD"
SCOPE_ID=$(kcadm.sh get client-scopes -r building-os --fields id,name --format csv --noquotes \
  | awk -F, '$2=="building-os-api"{print $1}')
MAPPER_ID=$(kcadm.sh get "client-scopes/$SCOPE_ID/protocol-mappers/models" -r building-os \
  --fields id,name --format csv --noquotes | awk -F, '$2=="building-os-permissions"{print $1}')
kcadm.sh update "client-scopes/$SCOPE_ID/protocol-mappers/models/$MAPPER_ID" -r building-os \
  -s 'config."aggregate.attrs"=true'
```

Access can widen for two kinds of user, so review both before applying it:

- users in **more than one** permission-carrying group (previously only one group counted);
- users who have their **own** `permissions` attribute **and** belong to any permission-carrying group.
  Previously the user attribute replaced the group attribute entirely, so a narrower per-user value
  (e.g. one building) could deliberately mask a broader group grant; now both are unioned.

Tokens issued before the change keep the old claim until they expire.

## Azure AD Migration Source

| Azure AD concept | Keycloak replacement |
|---|---|
| App Registration for web dashboard | `web-client` public client (also serves the `/admin` workspace) |
| API exposed scope / audience | `building-os-api` client scope + `api-server` audience |
| App roles / group assignments | Realm roles + group membership |
| Optional token claims | OIDC protocol mappers |
| Managed Identity / workload identity | `api-server` service account with client credentials |

No Azure SDK, MSAL, Microsoft.Identity.Web, or Microsoft Graph dependency is
required for the OSS identity path.

## Service Accounts

The `api-server` confidential client has `serviceAccountsEnabled=true`. Its
client secret in `realm.json` is a local-development placeholder and must be
overridden in real environments through Keycloak administration or secret
management.

## HITL Sign-Off

Security and operations reviewers must confirm.
**Signed off 2026-06-14 (interactive HITL review):** all four boxes ticked.

- [x] **Token claims match `AuthorizationContext` expectations.** _Signed off 2026-06-14: middleware now
      reads the Keycloak-native `building_os_role` / `permissions` claims (Azure-AD names as fallback);
      Admin API is a cached fallback only. See "Token claims → AuthorizationContext" above._
- [x] **Realm/client topology** (`web-client` public, `api-server` confidential; no separate admin
      client) is acceptable for the deployment environment. _(as-built, OK)_
- [x] **Service account credential handling**: `api-server` confidential client secret comes from
      deployment secrets, not source control (the `realm.json` value is a local-dev placeholder). _(as-built, OK)_
- [x] **Role and permission mappings preserve least privilege** (admin `*:*:*`; operator read + control;
      viewer read-only — matching the realm seed attributes). _(as-built, OK)_
      _Superseded by #505 (2026-09-29): the seeded operator/viewer wildcard grants never took effect
      (the API does not interpret `*`) and were removed along with the `testoperator` user._

