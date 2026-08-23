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

  // Since #344 `value` itself is the union, so its runtime type discriminates and a contradictory
  // legacy sibling no longer wins. Neither server shape can produce this row (Apply always sets
  // exactly one payload field, and the union server puts the reading in `value`), so it is garbage
  // either way — but pinning it keeps the two resolvers from drifting on it.
  it("lets the union value win over a contradictory legacy sibling", () => {
    expect(resolveTelemetryValue({ value: 42, valueText: "auto" })).toEqual({
      kind: "number",
      value: 42,
    });
  });

  it("rejects a value whose runtime type the discriminant denies", () => {
    expect(
      resolveTelemetryValue({ value: 42, valueType: "string", valueText: "auto" }),
    ).toEqual({ kind: "none" });
  });

  // ── Backward compatibility with a pre-#344 server (the cases that actually matter) ──
  //
  // The API dual-emits this release, so an old client sees the shape it expects. The reverse — a new
  // client against an older server, which sends `value: null` for a non-numeric point — has no
  // compile signal, so it is pinned here.
  it("still resolves a pre-#344 server's string reading (value null, payload in valueText)", () => {
    expect(
      resolveTelemetryValue({ value: null, valueType: "string", valueText: "auto" }),
    ).toEqual({ kind: "string", value: "auto" });
  });

  it("still resolves a pre-#344 server's boolean reading (value null, payload in valueBool)", () => {
    expect(
      resolveTelemetryValue({ value: null, valueType: "boolean", valueBool: false }),
    ).toEqual({ kind: "boolean", value: false });
  });

  it("resolves a dual-emitted row identically whichever half it reads", () => {
    // What the server actually sends this release: the union AND the legacy trio, agreeing.
    expect(
      resolveTelemetryValue({ value: "auto", valueType: "string", valueText: "auto" }),
    ).toEqual({ kind: "string", value: "auto" });
    expect(
      resolveTelemetryValue({ value: true, valueType: "boolean", valueBool: true }),
    ).toEqual({ kind: "boolean", value: true });
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
