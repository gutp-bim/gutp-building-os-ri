import type { BrowserContext } from "@playwright/test";

/**
 * Logged-in-state injection for E2E (#159).
 *
 * The web client never verifies the access-token signature — `middleware.ts` only
 * checks that the `oidc.access_token` cookie EXISTS, and `claims.ts` base64-decodes
 * the payload for role/permissions. So a self-made unsigned JWT is enough to appear
 * fully logged in, with no Keycloak round-trip. We set both:
 *   1. the cookie (middleware gate + Bearer for API clients), and
 *   2. the oidc-client-ts user in localStorage (so the client-side auth provider /
 *      AppShell resolve role, permissions, and display name).
 */
export type Role = "admin" | "operator" | "viewer";

const APP_ORIGIN = process.env.E2E_BASE_URL ?? "http://localhost:3000";
const KEYCLOAK_AUTHORITY =
  process.env.NEXT_PUBLIC_KEYCLOAK_AUTHORITY ?? "http://localhost:8080/realms/building-os";
const KEYCLOAK_CLIENT_ID = process.env.NEXT_PUBLIC_KEYCLOAK_CLIENT_ID ?? "web-client";
const OIDC_TOKEN_COOKIE = "oidc.access_token";

function b64url(value: unknown): string {
  return Buffer.from(JSON.stringify(value)).toString("base64url");
}

/** Unsigned JWT carrying the Building OS role + permission claims. */
export function makeAccessToken(role: Role, permissions: string[], name: string): string {
  const now = Math.floor(Date.now() / 1000);
  const header = { alg: "none", typ: "JWT" };
  const payload = {
    sub: `e2e-${role}`,
    preferred_username: name,
    name,
    building_os_role: role,
    permissions,
    iat: now,
    exp: now + 3600,
  };
  return `${b64url(header)}.${b64url(payload)}.e2e`;
}

function oidcUserJson(token: string, name: string): string {
  const now = Math.floor(Date.now() / 1000);
  return JSON.stringify({
    access_token: token,
    token_type: "Bearer",
    scope: "openid profile email building-os-api",
    expires_at: now + 3600,
    profile: { sub: "e2e", name, preferred_username: name },
  });
}

/** The oidc-client-ts WebStorageStateStore user key: `oidc.user:{authority}:{client_id}`. */
export function oidcUserStorageKey(): string {
  return `oidc.user:${KEYCLOAK_AUTHORITY}:${KEYCLOAK_CLIENT_ID}`;
}

/**
 * Make the browser context appear logged in as `role`. Call before the first
 * navigation. Returns the token so tests can assert Bearer forwarding if needed.
 */
export async function loginAs(
  context: BrowserContext,
  role: Role,
  opts: { permissions?: string[]; name?: string } = {},
): Promise<string> {
  const name = opts.name ?? `E2E ${role}`;
  const token = makeAccessToken(role, opts.permissions ?? [], name);
  await context.addCookies([{ name: OIDC_TOKEN_COOKIE, value: token, url: APP_ORIGIN }]);
  await context.addInitScript(
    ([userKey, userValue]) => {
      window.localStorage.setItem(userKey, userValue);
      // Suppress the first-login onboarding tour (#150) — its full-screen overlay
      // would otherwise intercept clicks and cover content under test.
      window.localStorage.setItem("buildingos.onboarding.completed.v1", "1");
    },
    [oidcUserStorageKey(), oidcUserJson(token, name)] as const,
  );
  return token;
}
