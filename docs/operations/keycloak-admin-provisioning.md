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

Neither `oss-stack/keycloak/realm.json` nor the Helm chart assigns them today: the realm's `api-server`
client has `serviceAccountsEnabled` but no service-account role mappings, the OSS compose stack does not set
`KEYCLOAK_ADMIN_CLIENT_ID`, and the Helm values leave it empty. Grant the roles when you configure the admin
client, e.g.:

```bash
kcadm.sh add-roles -r building-os --uusername service-account-<admin-client> \
  --cclientid realm-management --rolename view-users --rolename query-groups --rolename manage-users
```

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

