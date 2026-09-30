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

### Admin UI writes the same attributes (#519)

The `/admin` user management (`KeycloakUserManagementService`) reads and writes the **same**
`role` / `permissions` user attributes the mappers above put into the token. The names have one
definition, `KeycloakUserAttributes` (`DotNet/BuildingOS.Shared/Domain/UserManagement/`), used by both
the admin UI and the `AuthorizationContextMiddleware` Admin-API fallback, and a unit test pins it to
the mapper config in `oss-stack/keycloak/realm.json`. So a grant made in `/admin` reaches the
`permissions` claim, and it is unioned with the user's groups' `permissions` like any other (#508).

Before #519 the admin UI wrote `buildingos_role` / `buildingos_permissions` instead. Those never
reached the token, so a user who also got `building_os_role` from a group (e.g. `building-os-admins`)
silently lost every grant made in `/admin`, and which grants applied depended on whether the token
happened to carry the claim.

**Migration period — dual read.** The Admin-API path still honours the legacy attributes, with this
precedence:

| Value | Effective value |
|---|---|
| role | `role` when it has a non-empty value, otherwise `buildingos_role` |
| permissions | `permissions` ∪ `buildingos_permissions` (new values first, duplicates dropped) |

Any change made through `/admin` writes the merged values to `role` / `permissions` and removes that
user's `buildingos_*` attributes, so a permission removed in `/admin` cannot be merged back from the
legacy attribute. The token path does **not** read the legacy names: until a user is migrated, grants
held only in `buildingos_*` apply only when the token carries no `building_os_role` claim. Migrate them
with the procedure below.

**Realm user profile.** Keycloak 24+ keeps only the user attributes its user profile declares unless
the realm allows *unmanaged* attributes; without that, a `PUT` of `role` / `permissions` returns `204`
and stores nothing, and `role` in a realm import is dropped the same way. `realm.json` therefore sets
`unmanagedAttributePolicy: ADMIN_EDIT` — admins (the Admin API and the admin console) can read and
write these attributes, users cannot see or edit them in the account console. For the same reason the
admin UI writes back the **full** user representation it just read: since Keycloak 24 a `PUT` carrying
`attributes` is a full profile update, and an absent `email` / `firstName` / `lastName` is cleared,
which then blocks the user's login ("Account is not fully set up").

#### Migrating an existing realm (`kcadm.sh` + `jq`)

`--import-realm` skips an existing realm, so apply both steps to a running Keycloak. Verified against
Keycloak 26.7.

```bash
kcadm.sh config credentials --server "$KC_URL" --realm master --user "$KC_ADMIN" --password "$KC_ADMIN_PASSWORD"
REALM=building-os

# 1. Let the Admin API store unmanaged attributes (only admins can read or write them).
kcadm.sh get users/profile -r "$REALM" \
  | jq '.unmanagedAttributePolicy = "ADMIN_EDIT"' \
  | kcadm.sh update users/profile -r "$REALM" -f -

# 2. Copy buildingos_* into role / permissions (same precedence as the dual read) and drop the
#    legacy attributes. `kcadm.sh update -f` sends the full representation, so profile fields survive.
kcadm.sh get users -r "$REALM" --limit 100000 \
  | jq -c '.[] | select(.attributes.buildingos_role or .attributes.buildingos_permissions)' \
  | while read -r user; do
      id=$(jq -r .id <<<"$user")
      jq '.attributes |= (
            . as $a
            | .role = (if ([($a.role // [])[] | select(. != "")] | length) > 0
                       then $a.role else ($a.buildingos_role // []) end)
            | .permissions = ((($a.permissions // []) + ($a.buildingos_permissions // [])) | unique)
            | del(.buildingos_role, .buildingos_permissions)
            | with_entries(select(.value | length > 0)))' <<<"$user" \
        | kcadm.sh update "users/$id" -r "$REALM" -f -
      echo "migrated $(jq -r .username <<<"$user")"
    done

# 3. Verify: prints nothing once every user is migrated.
kcadm.sh get users -r "$REALM" --limit 100000 \
  | jq -r '.[] | select(.attributes.buildingos_role or .attributes.buildingos_permissions) | .username'
```

On a Keycloak 24+ realm that never had the policy, the admin UI's writes were discarded outright, so
step 2 finds nothing to migrate: re-grant those users in `/admin` after step 1. Step 2 matters for a
realm that stored the legacy attributes (an older Keycloak, or unmanaged attributes already enabled).

Access can widen for a migrated user in the same way as the #508 change above: their `permissions`
now reach the token and are unioned with their groups'. Review users that carry `buildingos_permissions`
before step 2. Tokens issued before the migration keep the old claims until they expire, and the
API server caches an Admin-API resolution for 5 minutes.

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

