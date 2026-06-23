import { describe, expect, it } from "vitest";
import { validateSettingInput } from "./validate";

describe("validateSettingInput", () => {
  it("normalizes booleans and rejects non-booleans", () => {
    expect(validateSettingInput("Boolean", "TRUE")).toEqual({ ok: true, normalized: "true" });
    expect(validateSettingInput("Boolean", "false")).toEqual({ ok: true, normalized: "false" });
    expect(validateSettingInput("Boolean", "yes").ok).toBe(false);
  });

  it("accepts numbers and rejects non-numbers", () => {
    expect(validateSettingInput("Number", "300")).toEqual({ ok: true, normalized: "300" });
    expect(validateSettingInput("Number", "12.5")).toEqual({ ok: true, normalized: "12.5" });
    expect(validateSettingInput("Number", "abc").ok).toBe(false);
    expect(validateSettingInput("Number", "").ok).toBe(false);
  });

  it("accepts any string", () => {
    expect(validateSettingInput("String", "anything")).toEqual({ ok: true, normalized: "anything" });
  });
});
