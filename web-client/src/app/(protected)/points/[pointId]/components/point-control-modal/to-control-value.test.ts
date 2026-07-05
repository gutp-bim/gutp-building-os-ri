import { describe, expect, it } from "vitest";
import { toControlValue } from "./to-control-value";

describe("toControlValue", () => {
  it("passes a numeric value through unchanged", () => {
    expect(toControlValue(21.5)).toBe(21.5);
  });

  it("maps true to 1", () => {
    expect(toControlValue(true)).toBe(1);
  });

  it("maps false to 0", () => {
    expect(toControlValue(false)).toBe(0);
  });
});
