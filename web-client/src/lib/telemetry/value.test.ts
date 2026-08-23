import { describe, expect, it } from "vitest";
import {
  formatResolvedValue,
  formatTelemetryValue,
  isNonNumericValue,
  resolveTelemetryValue,
} from "./value";

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

  // Characterization: pins the precedence the doc comment used to contradict. A row reaching the
  // client with no valueType is a pre-#152 legacy row, and those carry no valueText/valueBool at
  // all (TelemetryValueKind.Apply always sets ValueType and exactly one payload field). So a
  // populated text/bool field is the stronger signal when the discriminant is missing.
  it("prefers a populated valueText/valueBool over a stray numeric value when valueType is absent", () => {
    expect(resolveTelemetryValue({ value: 42, valueText: "auto" })).toEqual({
      kind: "string",
      value: "auto",
    });
    expect(resolveTelemetryValue({ value: 1, valueBool: false })).toEqual({
      kind: "boolean",
      value: false,
    });
  });

  it("trusts an explicit discriminant over a populated field of another kind", () => {
    expect(
      resolveTelemetryValue({ value: 42, valueType: "number", valueText: "auto" }),
    ).toEqual({ kind: "number", value: 42 });
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

describe("formatTelemetryValue", () => {
  it("formats each kind for display", () => {
    expect(formatTelemetryValue({ value: 21.5, valueType: "number" })).toBe("21.5");
    expect(formatTelemetryValue({ valueType: "string", valueText: "auto" })).toBe("auto");
    expect(formatTelemetryValue({ valueType: "boolean", valueBool: true })).toBe("ON");
    expect(formatTelemetryValue({ valueType: "boolean", valueBool: false })).toBe("OFF");
    expect(formatTelemetryValue({})).toBeNull();
  });
});

describe("formatResolvedValue", () => {
  // Callers holding an already-resolved value (the TelemetryLatestSample domain type) format it
  // without re-deriving the discriminant.
  it("formats an already-resolved value without re-resolving", () => {
    expect(formatResolvedValue({ kind: "number", value: 0 })).toBe("0");
    expect(formatResolvedValue({ kind: "string", value: "auto" })).toBe("auto");
    expect(formatResolvedValue({ kind: "boolean", value: true })).toBe("ON");
    expect(formatResolvedValue({ kind: "boolean", value: false })).toBe("OFF");
    expect(formatResolvedValue({ kind: "none" })).toBeNull();
  });
});
