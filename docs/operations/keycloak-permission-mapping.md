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

| Role | Keycloak realm role | Permission attributes |
|---|---|---|
| Admin | `building-os-admin` | `*:*:*` |
| Operator | `building-os-operator` | `building:*:read`, `floor:*:read`, `space:*:read`, `device:*:read,control`, `point:*:read,write,control` |
| Viewer | `building-os-viewer` | `building:*:read`, `floor:*:read`, `space:*:read`, `device:*:read`, `point:*:read` |

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
| `permissions` (multivalued) | user attr `permissions` | `Permissions` |
| `idtyp=app` (client credentials) | — | `Role=admin` (service account) |

The middleware reads these **Keycloak-native** names first and falls back to the legacy
Azure-AD optional-claim names (`extension_BuildingOS_role` / `extension_BuildingOS_permissions`,
still emitted by `TestAuthenticationHandler`) for backward compatibility. Because the role/permissions
travel in the token, the common path needs **no per-request Keycloak Admin API call**; the Admin API
(`KeycloakUserManagementService`) is a fallback only (for tokens without the claims) and its result is
cached for 5 minutes. _(#10 sign-off fix, 2026-06-14: previously the middleware read only the Azure-AD
names, so real Keycloak tokens missed the claim path and every request hit the Admin API.)_

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

