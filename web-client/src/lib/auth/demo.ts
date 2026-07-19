/**
 * Demo-mode auto-login (#161 案A / demo profile).
 *
 * The DEFAULT stack is Keycloak-unified (#161 案B): both API and Web require a real login. The **demo**
 * profile (`make demo`) is meant to be zero-friction, so the API runs with `DISABLE_AUTH=true` and the
 * Web Client skips the Keycloak flow and auto-logs-in as a demo admin. Because the demo API never
 * validates the token, the client uses a synthetic, **unsigned** access token carrying admin claims.
 *
 * This path is **strictly demo-gated**: it only activates when the build-time flag
 * `NEXT_PUBLIC_DEMO_MODE === "true"` (set only by the demo compose overlay). In every real build the
 * flag is false, so `isDemoMode()` is false and none of this is reachable. It is NEVER a production
 * auth path — a persistent banner tells the user the auth flow is being skipped.
 */
import { OIDC_TOKEN_COOKIE } from "./oidc-config";

export function isDemoMode(): boolean {
  return process.env.NEXT_PUBLIC_DEMO_MODE === "true";
}

/** The synthetic demo user's display profile (also encoded into the token). */
export const DEMO_USER_PROFILE = {
  name: "デモ管理者",
  preferred_username: "demo-admin",
} as const;

function base64UrlEncode(value: unknown): string {
  const bytes = new TextEncoder().encode(JSON.stringify(value));
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary)
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
}

/**
 * Builds a synthetic, unsigned JWT access token with admin claims for demo mode. The signature segment
 * is a literal `"demo"` (never verified — the demo API runs DISABLE_AUTH; the client middleware/claims
 * decoder never checks signatures). Shape matches the Keycloak claims the app reads
 * (`building_os_role`, `permissions`, `name`/`preferred_username`).
 */
export function buildDemoAccessToken(): string {
  const header = { alg: "none", typ: "JWT" };
  const nowSeconds = Math.floor(Date.now() / 1000);
  const payload = {
    sub: "demo-admin",
    name: DEMO_USER_PROFILE.name,
    preferred_username: DEMO_USER_PROFILE.preferred_username,
    building_os_role: "admin",
    permissions: [] as string[],
    iss: "building-os-demo",
    iat: nowSeconds,
    exp: nowSeconds + 60 * 60 * 24 * 365, // 1 year — the demo session never expires in practice.
  };
  return `${base64UrlEncode(header)}.${base64UrlEncode(payload)}.demo`;
}

export { OIDC_TOKEN_COOKIE };
