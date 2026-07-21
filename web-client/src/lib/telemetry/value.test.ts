import { describe, expect, it } from "vitest";
import { isNonNumericValue, resolveTelemetryValue } from "./value";

describe("resolveTelemetryValue", () => {
  it("resolves a numeric value with explicit valueType", () => {
    expect(resolveTelemetryValue({ value: 21.5, valueType: "number" })).toEqual({
      kind: "number",
      value: 21.5,
    });
  });

  it("treats an absent valueType with a numeric value as number (legacy default)", () => {
    expect(resolveTelemetryValue({ value: 0 })).toEqual({ kind: "number", value: 0 });
  });

  it("resolves a string value", () => {
    expect(
      resolveTelemetryValue({ valueType: "string", valueText: "auto" }),
    ).toEqual({ kind: "string", value: "auto" });
  });

  it("resolves a boolean value (including false)", () => {
    expect(
      resolveTelemetryValue({ valueType: "boolean", valueBool: false }),
    ).toEqual({ kind: "boolean", value: false });
  });

  it("infers string/boolean from the populated field when valueType is absent", () => {
    expect(resolveTelemetryValue({ valueText: "off" })).toEqual({
      kind: "string",
      value: "off",
    });
    expect(resolveTelemetryValue({ valueBool: true })).toEqual({
      kind: "boolean",
      value: true,
    });
  });

  it("returns none when nothing is representable", () => {
    expect(resolveTelemetryValue({})).toEqual({ kind: "none" });
    expect(resolveTelemetryValue({ value: null, valueText: null })).toEqual({
      kind: "none",
    });
  });

  it("returns none when valueType says string but no text is present", () => {
    expect(resolveTelemetryValue({ valueType: "string" })).toEqual({ kind: "none" });
  });
});

describe("isNonNumericValue", () => {
  it("is true only for string/boolean readings", () => {
    expect(isNonNumericValue({ value: 1, valueType: "number" })).toBe(false);
    expect(isNonNumericValue({ valueType: "string", valueText: "a" })).toBe(true);
    expect(isNonNumericValue({ valueType: "boolean", valueBool: false })).toBe(true);
    expect(isNonNumericValue({})).toBe(false);
  });
});
