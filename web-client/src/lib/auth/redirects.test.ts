import { describe, expect, it } from "vitest";
import { POST_LOGIN_PATH } from "./redirects";

describe("POST_LOGIN_PATH (#191)", () => {
  it("lands every authenticated entry point on the operator home", () => {
    // #178/#185 unified post-login landing on /home. This constant is the single source of truth so
    // sign-in re-visits and the OIDC callback can't drift back to /buildings (which only redirects
    // to /resources, producing a two-hop landing that varies by entry point).
    expect(POST_LOGIN_PATH).toBe("/home");
  });
});
