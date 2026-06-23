import { describe, expect, it } from "vitest";
import { decodeJwtPayload, parseAuthClaims } from "./claims";

/** Builds an unsigned JWT (`header.payload.sig`) for a given payload. */
function makeJwt(payload: Record<string, unknown>): string {
  const header = Buffer.from(JSON.stringify({ alg: "none", typ: "JWT" })).toString("base64url");
  const body = Buffer.from(JSON.stringify(payload)).toString("base64url");
  return `${header}.${body}.signature`;
}

describe("decodeJwtPayload", () => {
  it("decodes a well-formed token payload", () => {
    const token = makeJwt({ sub: "u1", building_os_role: "operator" });
    expect(decodeJwtPayload(token)).toMatchObject({ sub: "u1", building_os_role: "operator" });
  });

  it("returns null for a malformed token", () => {
    expect(decodeJwtPayload("not-a-jwt")).toBeNull();
    expect(decodeJwtPayload("")).toBeNull();
    expect(decodeJwtPayload("a.!!!notbase64!!!.c")).toBeNull();
  });
});

describe("parseAuthClaims", () => {
  it("returns empty claims for null/undefined/empty token", () => {
    expect(parseAuthClaims(null)).toEqual({ role: null, permissions: [] });
    expect(parseAuthClaims(undefined)).toEqual({ role: null, permissions: [] });
    expect(parseAuthClaims("")).toEqual({ role: null, permissions: [] });
  });

  it("extracts role and permissions", () => {
    const token = makeJwt({
      building_os_role: "admin",
      permissions: ["*:*:*", "point:*:read,write,control"],
    });
    expect(parseAuthClaims(token)).toEqual({
      role: "admin",
      permissions: ["*:*:*", "point:*:read,write,control"],
    });
  });

  it("normalizes role case and rejects unknown roles", () => {
    expect(parseAuthClaims(makeJwt({ building_os_role: "OPERATOR" })).role).toBe("operator");
    expect(parseAuthClaims(makeJwt({ building_os_role: "superuser" })).role).toBeNull();
    expect(parseAuthClaims(makeJwt({})).role).toBeNull();
  });

  it("wraps a single permission string into an array", () => {
    expect(parseAuthClaims(makeJwt({ permissions: "building:*:read" })).permissions).toEqual([
      "building:*:read",
    ]);
  });

  it("ignores non-string permission entries", () => {
    expect(
      parseAuthClaims(makeJwt({ permissions: ["building:*:read", 42, null] })).permissions,
    ).toEqual(["building:*:read"]);
  });
});
