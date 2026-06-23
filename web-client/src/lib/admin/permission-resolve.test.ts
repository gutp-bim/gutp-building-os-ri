import { describe, expect, it } from "vitest";
import { collectResolvableIds, resolveDisplay } from "./permission-resolve";

describe("collectResolvableIds", () => {
  it("collects unique non-group resource ids", () => {
    expect(
      collectResolvableIds(["d:hash1:rw", "b:hash2:r", "d:hash1:r", "g:grp-1:r"]),
    ).toEqual(["hash1", "hash2"]);
  });

  it("skips malformed strings", () => {
    expect(collectResolvableIds(["bad", "d:hash:r"])).toEqual(["hash"]);
  });
});

describe("resolveDisplay", () => {
  const map = {
    hash1: { originalId: "dev-1", resourceType: "device", displayName: "1F AHU" },
    hash2: { originalId: "bldg-1", resourceType: "building", displayName: null },
  };

  it("prefers the display name and exposes the hash as title", () => {
    expect(resolveDisplay("hash1", map)).toEqual({ label: "1F AHU", title: "hash1" });
  });

  it("falls back to the original id when there is no display name", () => {
    expect(resolveDisplay("hash2", map)).toEqual({ label: "bldg-1", title: "hash2" });
  });

  it("falls back to the raw id when unresolved or no map", () => {
    expect(resolveDisplay("unknown", map)).toEqual({ label: "unknown" });
    expect(resolveDisplay("hash1")).toEqual({ label: "hash1" });
  });
});
