# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| `main` branch | ✅ |
| Older tagged releases | ⚠️ Best-effort |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub Issues.**

Report vulnerabilities privately via [GitHub Security Advisories](../../security/advisories/new).

Include:
- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

We will acknowledge receipt within 72 hours and aim to release a fix within 14 days for critical issues.

## Scope

This policy covers:
- The Building OS API Server, ConnectorWorker, and GatewayBridge (.NET services)
- The web-client (Next.js)
- The `docker-compose.oss.yaml` local stack configuration

Out of scope:
- Third-party dependencies (report to their upstream maintainers)
- Issues only reproducible with `DISABLE_AUTH=true` (development-only flag, not for production)

## Security Notes for Operators

- **Never use default credentials** (`buildingos` / `buildingos123`) in production. Override all defaults via environment variables or a secrets manager.
- The `DISABLE_AUTH=true` flag is for local development only. Production deployments must use Keycloak OIDC.
- `docker-compose.oss.yaml` is a local development stack. For production, use the Helm charts in `kubernetes/helm/` with proper secret injection.
- The Keycloak `realm.json` in `oss-stack/keycloak/` contains example users (`admin` / `testoperator`) with development passwords. Replace these before any internet-facing deployment.
- The `oss-stack/keycloak/realm.json` client secret (`change-me-in-production`) **must** be rotated before production use.
