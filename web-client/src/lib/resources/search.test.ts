import { describe, expect, it } from "vitest";
import { normalizeSearchParams, normalizeTags } from "./search";

describe("normalizeSearchParams", () => {
  it("trims q and drops it when blank", () => {
    expect(normalizeSearchParams({ q: "  vav  " }).q).toBe("vav");
    expect(normalizeSearchParams({ q: "   " }).q).toBeUndefined();
    expect(normalizeSearchParams({}).q).toBeUndefined();
  });

  it("defaults limit to 50 and clamps to 1..200", () => {
    expect(normalizeSearchParams({}).limit).toBe(50);
    expect(normalizeSearchParams({ limit: 0 }).limit).toBe(1);
    expect(normalizeSearchParams({ limit: 5000 }).limit).toBe(200);
    expect(normalizeSearchParams({ limit: 25 }).limit).toBe(25);
  });

  it("defaults offset to 0 and floors negatives to 0", () => {
    expect(normalizeSearchParams({}).offset).toBe(0);
    expect(normalizeSearchParams({ offset: -3 }).offset).toBe(0);
    expect(normalizeSearchParams({ offset: 20 }).offset).toBe(20);
  });

  it("passes through type and buildingId, dropping empty strings", () => {
    expect(normalizeSearchParams({ type: "point" }).type).toBe("point");
    expect(
      normalizeSearchParams({ buildingId: "" }).buildingId,
    ).toBeUndefined();
    expect(normalizeSearchParams({ buildingId: "urn:b1" }).buildingId).toBe(
      "urn:b1",
    );
  });

  it("normalizes tags: trim, drop blanks, dedupe; omits when empty (#332)", () => {
    expect(normalizeSearchParams({}).tag).toBeUndefined();
    expect(normalizeSearchParams({ tags: [] }).tag).toBeUndefined();
    expect(normalizeSearchParams({ tags: ["  ", ""] }).tag).toBeUndefined();
    expect(
      normalizeSearchParams({ tags: [" hvac ", "temperature", "hvac"] }).tag,
    ).toEqual(["hvac", "temperature"]);
  });
});

describe("normalizeTags", () => {
  it("trims, drops blanks, de-duplicates preserving order", () => {
    expect(normalizeTags(undefined)).toEqual([]);
    expect(normalizeTags([" a ", "b", "a", "  ", "b"])).toEqual(["a", "b"]);
  });
});
