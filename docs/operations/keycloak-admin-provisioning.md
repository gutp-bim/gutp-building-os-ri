# Keycloak Admin Provisioning

This runbook describes how to reproduce and maintain the Building OS Keycloak
realm.

## Realm Import

Local OSS startup imports `oss-stack/keycloak/realm.json` automatically:

```bash
make local-up-oss
```

The Keycloak service runs with:

```text
start-dev --import-realm
```

The import file is mounted read-only at `/opt/keycloak/data/import/realm.json`.
For a clean local re-import, stop the stack and remove the `keycloak_data`
volume before starting again.

## Role Synchronization

Role and permission changes should be made in the realm JSON first, reviewed,
and then applied through the Keycloak Admin API in managed environments.

Minimum synchronization flow:

1. Create or update realm roles.
2. Create or update groups for admin/operator/viewer cohorts.
3. Write `role` and `permissions` attributes to users or groups.
4. Verify access tokens include `building_os_role` and `permissions`.
5. Run API authorization tests against representative users.

## Admin API Client

The API server uses Keycloak Admin REST APIs through
`KeycloakUserManagementService`. Required environment variables:

| Variable | Purpose |
|---|---|
| `KEYCLOAK_AUTHORITY` | Realm issuer base URL |
| `KEYCLOAK_REALM` | Realm name, normally `building-os` |
| `KEYCLOAK_ADMIN_CLIENT_ID` | Confidential admin client ID |
| `KEYCLOAK_ADMIN_CLIENT_SECRET` | Confidential admin client secret |

### Service-account roles (#532)

The admin client authenticates with `client_credentials`, so its **service-account user** needs these
`realm-management` client roles:

| Role | Used for |
|---|---|
| `view-users` | `GET users`, `GET users/{id}` (the user list, detail and the Admin-API authorization fallback) |
| `query-groups` | `GET users/{id}/groups`, `GET groups/{id}`, `GET group-by-path/...` (the role inherited from a group) |
| `manage-users` | `PUT users/{id}` (role / permission / enabled writes) |

No other role is needed: `view-users` already covers the user list/search, and nothing grants
`realm-admin`, `manage-clients`, `manage-realm` or any realm role. With exactly these three roles, a
client_credentials token from `api-server` gets 200/204 on every endpoint above and 403 on `GET clients`,
`GET roles` and `PUT` of the realm (verified against `quay.io/keycloak/keycloak:26.7` with the realm import).

#### What the OSS stack ships

The recommended setup reuses the existing confidential `api-server` client (`serviceAccountsEnabled: true`)
as the admin client, so there is no second client to provision:

- `oss-stack/keycloak/realm.json` declares its service-account user in the `--import-realm` format and grants
  exactly the three roles:

  ```json
  {
    "username": "service-account-api-server",
    "enabled": true,
    "serviceAccountClientId": "api-server",
    "clientRoles": { "realm-management": ["view-users", "query-groups", "manage-users"] }
  }
  ```

- `docker-compose.oss.yaml` sets `KEYCLOAK_ADMIN_CLIENT_ID=${KEYCLOAK_ADMIN_CLIENT_ID:-api-server}` and
  `KEYCLOAK_ADMIN_CLIENT_SECRET=${KEYCLOAK_ADMIN_CLIENT_SECRET:-change-me-in-production}` (the realm's dev
  secret) on the API server, so user management works out of the box.
- The Helm chart sets `apiServer.env.KEYCLOAK_ADMIN_CLIENT_ID: api-server`; the secret stays in
  `apiServer.secretEnv` and is read from the `building-os-api-secrets` Secret, key
  `keycloak-admin-client-secret`. The client id is no longer read from that Secret.

`Tools/e2e-performance/tests/test_keycloak_admin_client_config.py` pins these three pieces.

The realm import only applies to a fresh realm (`IGNORE_EXISTING`). An existing local `keycloak_data`
volume keeps the old realm without the role mappings — remove the volume, or grant the roles by hand
(below).

#### What production must do

- **Rotate the `api-server` client secret.** `change-me-in-production` is public in this repository.
  Regenerate it (Admin Console → Clients → `api-server` → Credentials → Regenerate, or
  `kcadm.sh create clients/<id>/client-secret -r building-os`), put the new value in the
  `building-os-api-secrets` Secret as `keycloak-admin-client-secret`, and restart the API server. The same
  client authenticates the API server's JWT audience, so the secret is the only thing that changes.
- If the realm was not created from `realm.json` (or predates this change), grant the roles explicitly:

  ```bash
  kcadm.sh add-roles -r building-os --uusername service-account-api-server \
    --cclientid realm-management --rolename view-users --rolename query-groups --rolename manage-users
  ```

- If you use a dedicated admin client instead, give its service account the same three roles and override
  `KEYCLOAK_ADMIN_CLIENT_ID` (Helm `apiServer.env`, compose `KEYCLOAK_ADMIN_CLIENT_ID`).

#### OIDC client management is not covered

`KeycloakOidcClientService` (`/platform` OIDC client management, #324) uses the same admin client but calls
`/admin/realms/{realm}/clients…`, which needs `view-clients` / `manage-clients`. Those are deliberately **not**
granted: `manage-clients` can rewrite any client in the realm, including `api-server` itself. With the shipped
wiring that screen's Keycloak calls answer 403, which the API maps to **503** with the reason ("admin client
'api-server' is not permitted to manage OIDC clients … grant manage-clients"), so the screen explains the gap
instead of failing. Grant `manage-clients` to the service account only in a deployment that needs that
screen and accepts the broader privilege.

Without `query-groups` the group lookups answer 403. Since #532 that no longer fails the request: a user with
no own `role` is listed with a blank role (warning log + `building_os_user_management_group_lookup_failures_total{reason="forbidden"}`),
the lockout guard treats that user's role as possibly admin (and never as a remaining admin), and the
Admin-API authorization fallback authorizes from the user's own attributes instead of dropping them to
`role=user`. A steady non-zero `reason="forbidden"` rate means the role is missing.

## Operational Checks

- The admin client must not be a public client.
- Client secrets must come from deployment secrets, not source control.
- Realm import JSON is the reviewed source of truth.
- Production updates should be applied through CI/CD or a controlled runbook.

