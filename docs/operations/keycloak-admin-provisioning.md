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

## Operational Checks

- The admin client must not be a public client.
- Client secrets must come from deployment secrets, not source control.
- Realm import JSON is the reviewed source of truth.
- Production updates should be applied through CI/CD or a controlled runbook.

