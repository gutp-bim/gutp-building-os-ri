import { describe, expect, it } from "vitest";
import {
  formatResolvedValue,
  resolveStateValue,
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

  // A MIXED AGGREGATE BUCKET carries both an average and a state representative: the store sets
  // `value = avg` unconditionally while tagging the bucket by its last-in-bucket reading. The union
  // takes the numeric half — `value` has a published numeric meaning at Hour/Day granularity — and
  // the state half is read via resolveStateValue instead.
  it("yields the numeric average for a mixed aggregate bucket, not its state representative", () => {
    expect(
      resolveTelemetryValue({ value: 42, valueType: "string", valueText: "auto" }),
    ).toEqual({ kind: "number", value: 42 });
  });

  it("yields the representative for a purely non-numeric bucket (no average)", () => {
    expect(
      resolveTelemetryValue({ value: null, valueType: "string", valueText: "auto" }),
    ).toEqual({ kind: "string", value: "auto" });
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

describe("resolveStateValue", () => {
  it("reads the state representative of a mixed aggregate bucket, ignoring the average", () => {
    expect(
      resolveStateValue({ value: 42, valueType: "string", valueText: "auto" }),
    ).toEqual({ kind: "string", value: "auto" });
    expect(
      resolveStateValue({ value: 42, valueType: "boolean", valueBool: true }),
    ).toEqual({ kind: "boolean", value: true });
  });

  it("reads a post-#344 raw non-numeric reading from the union value", () => {
    expect(resolveStateValue({ value: "auto", valueType: "string" })).toEqual({
      kind: "string",
      value: "auto",
    });
  });

  it("has no representative for a purely numeric row", () => {
    expect(resolveStateValue({ value: 21.5, valueType: "number" })).toEqual({ kind: "none" });
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
