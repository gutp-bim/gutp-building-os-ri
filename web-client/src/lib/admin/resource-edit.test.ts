import { describe, expect, it } from "vitest";
import { validateResourceId } from "./resource-edit";

describe("validateResourceId", () => {
  it("rejects an empty or whitespace id", () => {
    expect(validateResourceId("").ok).toBe(false);
    expect(validateResourceId("   ").ok).toBe(false);
  });

  it("accepts a non-empty id", () => {
    expect(validateResourceId("bldg-1")).toEqual({ ok: true });
  });
});
