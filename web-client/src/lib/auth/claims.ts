/**
 * Building OS role, as carried by the Keycloak `building_os_role` access-token claim.
 * (See oss-stack/keycloak/realm.json — the building-os-role protocol mapper.)
 */
export type BuildingOsRole = "admin" | "operator" | "viewer";

export type AuthClaims = {
  role: BuildingOsRole | null;
  permissions: string[];
};

const KNOWN_ROLES: readonly BuildingOsRole[] = ["admin", "operator", "viewer"];

function base64UrlDecode(input: string): string {
  const padLength = input.length % 4 === 0 ? 0 : 4 - (input.length % 4);
  const base64 = input.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat(padLength);
  const binary = atob(base64);
  const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

/**
 * Decodes the payload of a JWT without verifying its signature. Returns `null` when the token
 * is malformed. Signature verification is the API server's job — this is only used to drive UI
 * gating, which is a convenience layer, never the authorization boundary.
 */
export function decodeJwtPayload(token: string): Record<string, unknown> | null {
  try {
    const parts = token.split(".");
    if (parts.length < 2 || !parts[1]) return null;
    const parsed: unknown = JSON.parse(base64UrlDecode(parts[1]));
    return typeof parsed === "object" && parsed !== null
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

function normalizeRole(raw: unknown): BuildingOsRole | null {
  if (typeof raw !== "string") return null;
  const lower = raw.trim().toLowerCase();
  return (KNOWN_ROLES as readonly string[]).includes(lower)
    ? (lower as BuildingOsRole)
    : null;
}

function normalizePermissions(raw: unknown): string[] {
  if (Array.isArray(raw)) return raw.filter((p): p is string => typeof p === "string");
  if (typeof raw === "string" && raw.length > 0) return [raw];
  return [];
}

/**
 * Extracts the Building OS role and permission strings from an access token. Degrades to
 * `{ role: null, permissions: [] }` for a missing or malformed token.
 */
export function parseAuthClaims(token: string | null | undefined): AuthClaims {
  if (!token) return { role: null, permissions: [] };
  const payload = decodeJwtPayload(token);
  if (!payload) return { role: null, permissions: [] };
  return {
    role: normalizeRole(payload["building_os_role"]),
    permissions: normalizePermissions(payload["permissions"]),
  };
}
