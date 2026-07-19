import { afterEach, describe, expect, it } from "vitest";
import { parseAuthClaims } from "./claims";
import { buildDemoAccessToken, isDemoMode } from "./demo";

describe("demo auth (#161)", () => {
  const original = process.env.NEXT_PUBLIC_DEMO_MODE;
  afterEach(() => {
    process.env.NEXT_PUBLIC_DEMO_MODE = original;
  });

  it("isDemoMode reflects NEXT_PUBLIC_DEMO_MODE (default off)", () => {
    process.env.NEXT_PUBLIC_DEMO_MODE = "true";
    expect(isDemoMode()).toBe(true);
    process.env.NEXT_PUBLIC_DEMO_MODE = "false";
    expect(isDemoMode()).toBe(false);
    delete process.env.NEXT_PUBLIC_DEMO_MODE;
    expect(isDemoMode()).toBe(false);
  });

  it("buildDemoAccessToken produces a 3-part JWT that decodes to admin claims", () => {
    const token = buildDemoAccessToken();
    expect(token.split(".")).toHaveLength(3);
    const claims = parseAuthClaims(token);
    expect(claims.role).toBe("admin");
    expect(claims.permissions).toEqual([]);
  });
});
