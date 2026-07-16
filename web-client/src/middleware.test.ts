import { describe, expect, it } from "vitest";
import { config } from "./middleware";

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
