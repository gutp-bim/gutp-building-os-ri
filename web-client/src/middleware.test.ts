import { NextRequest } from "next/server";
import { describe, expect, it } from "vitest";
import { config, middleware } from "./middleware";

function request(path: string, opts: { authenticated: boolean }): NextRequest {
  const headers: Record<string, string> = {};
  if (opts.authenticated) headers.cookie = "oidc.access_token=token-value";
  return new NextRequest(new URL(`http://localhost${path}`), { headers });
}

describe("middleware post-login redirect (#191/#200)", () => {
  it("sends an authenticated visitor of an auth page straight to /home, not via /", () => {
    const res = middleware(request("/sign-in", { authenticated: true }));
    // A 307/308 redirect carries the destination in Location.
    expect(res.headers.get("location")).toBe("http://localhost/home");
  });

  it("also short-circuits the OIDC callback path to /home when already authenticated", () => {
    const res = middleware(request("/auth/oidc-callback", { authenticated: true }));
    expect(res.headers.get("location")).toBe("http://localhost/home");
  });

  it("still redirects an unauthenticated visitor of a protected page to /sign-in", () => {
    const res = middleware(request("/home", { authenticated: false }));
    expect(res.headers.get("location")).toBe("http://localhost/sign-in");
  });
});

describe("middleware matcher (#193)", () => {
  it("no longer carves grpc-test out of auth enforcement", () => {
    // The grpc-test dev harness was excluded from the matcher, leaving it reachable unauthenticated.
    // With the page removed, that exclusion must be gone so no unauthenticated route survives.
    const matcher = config.matcher.join("\n");
    expect(matcher).not.toContain("grpc-test");
  });

  it("still excludes framework/internal paths from the guard", () => {
    const matcher = config.matcher.join("\n");
    expect(matcher).toContain("_next/static");
    expect(matcher).toContain("favicon.ico");
    expect(matcher).toContain("api");
  });
});
