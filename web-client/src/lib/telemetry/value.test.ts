import { describe, expect, it } from "vitest";
import {
  formatResolvedValue,
  formatTelemetryValue,
  isNonNumericValue,
  resolveStateValue,
  resolveTelemetryValue,
  type TelemetryWireValue,
} from "./value";

describe("resolveTelemetryValue", () => {
  it("resolves a numeric value with explicit valueType", () => {
    expect(resolveTelemetryValue({ value: 21.5, valueType: "number" })).toEqual(
      {
        kind: "number",
        value: 21.5,
      },
    );
  });

  it("treats an absent valueType with a numeric value as number", () => {
    expect(resolveTelemetryValue({ value: 0 })).toEqual({
      kind: "number",
      value: 0,
    });
  });

  // A raw non-numeric row repeats its reading in `state` (#359). Both fields are populated here
  // because that is what the server actually sends — asserting on a shape it never produces would
  // pin nothing.
  it("resolves a string value", () => {
    expect(
      resolveTelemetryValue({
        value: "auto",
        valueType: "string",
        state: "auto",
      }),
    ).toEqual({ kind: "string", value: "auto" });
  });

  it("resolves a boolean value (including false)", () => {
    expect(
      resolveTelemetryValue({
        value: false,
        valueType: "boolean",
        state: false,
      }),
    ).toEqual({ kind: "boolean", value: false });
  });

  it("returns none when nothing is representable", () => {
    expect(resolveTelemetryValue({})).toEqual({ kind: "none" });
    expect(resolveTelemetryValue({ value: null, state: null })).toEqual({
      kind: "none",
    });
  });

  // The discriminant describes `value`; it never decides where to look. A row that claims "string"
  // with no value is a server bug, and inventing a reading from `state` would hide it.
  it("returns none when valueType says string but no value is present", () => {
    expect(resolveTelemetryValue({ valueType: "string" })).toEqual({
      kind: "none",
    });
  });

  // A MIXED AGGREGATE BUCKET carries both an average and a state representative: the store sets
  // `value = avg` unconditionally while tagging the bucket by its last-in-bucket reading. The union
  // takes the numeric half — `value` has a published numeric meaning at Hour/Day granularity — and
  // the state half rides in `state`, read via resolveStateValue instead.
  it("yields the numeric average for a mixed aggregate bucket, not its state representative", () => {
    expect(
      resolveTelemetryValue({ value: 42, valueType: "number", state: "auto" }),
    ).toEqual({ kind: "number", value: 42 });
  });

  // A bucket with no numeric samples has no average (`Avg` is null), so the representative IS the
  // union value. This is why removing the legacy fields did not break non-numeric points.
  it("yields the representative for a purely non-numeric bucket (no average)", () => {
    expect(
      resolveTelemetryValue({
        value: "auto",
        valueType: "string",
        state: "auto",
      }),
    ).toEqual({ kind: "string", value: "auto" });
  });

  /**
   * #359 removed `valueText`/`valueBool` from the wire, so this client requires a server that sends
   * `value`/`state`. Against a pre-#359 server a non-numeric reading arrives with `value: null` and
   * the payload in a field this client no longer reads — and resolves to nothing.
   *
   * Pinned deliberately rather than papered over with a runtime fallback: the fallback chain is
   * exactly what #359 deleted, and re-adding it would make the removal pointless. What this test
   * records is the resulting constraint — the API server must not be OLDER than the web client.
   * Both ship from this repo through the same ArgoCD rollout, so the pairing is controllable; the
   * cast is here because TypeScript already rejects the old shape, which is the intended guard.
   */
  it("does not resolve a pre-#359 server's non-numeric reading (server must not lag the client)", () => {
    const preRemoval = {
      value: null,
      valueType: "string",
      valueText: "auto",
    } as TelemetryWireValue;

    expect(resolveTelemetryValue(preRemoval)).toEqual({ kind: "none" });
  });
});

describe("resolveStateValue", () => {
  it("reads the state representative of a mixed aggregate bucket, ignoring the average", () => {
    expect(
      resolveStateValue({ value: 42, valueType: "number", state: "auto" }),
    ).toEqual({ kind: "string", value: "auto" });
    expect(
      resolveStateValue({ value: 42, valueType: "number", state: true }),
    ).toEqual({ kind: "boolean", value: true });
  });

  // `false` is a reading, not "absent". A truthiness check here would silently drop every OFF
  // sample from the state timeline.
  it("treats a false state as a reading, not as absent", () => {
    expect(
      resolveStateValue({ value: 42, valueType: "number", state: false }),
    ).toEqual({ kind: "boolean", value: false });
  });

  // The raw row repeats its reading in `state`, so this resolver never needs to fall back to
  // `value` — that uniformity is what the duplication on the wire buys.
  it("reads a raw non-numeric reading from state", () => {
    expect(
      resolveStateValue({ value: "auto", valueType: "string", state: "auto" }),
    ).toEqual({ kind: "string", value: "auto" });
  });

  it("has no representative for a purely numeric row", () => {
    expect(
      resolveStateValue({ value: 21.5, valueType: "number", state: null }),
    ).toEqual({ kind: "none" });
  });

  it("has no representative when nothing is present", () => {
    expect(resolveStateValue({})).toEqual({ kind: "none" });
  });
});

describe("isNonNumericValue", () => {
  it("is true only for string/boolean readings", () => {
    expect(isNonNumericValue({ value: 1, valueType: "number" })).toBe(false);
    expect(
      isNonNumericValue({ value: "a", valueType: "string", state: "a" }),
    ).toBe(true);
    expect(
      isNonNumericValue({ value: false, valueType: "boolean", state: false }),
    ).toBe(true);
    expect(isNonNumericValue({})).toBe(false);
  });

  // It reads the state half, so a mixed aggregate bucket counts even though its union value is the
  // numeric average.
  it("is true for a mixed aggregate bucket", () => {
    expect(
      isNonNumericValue({ value: 42, valueType: "number", state: "auto" }),
    ).toBe(true);
  });
});

describe("formatTelemetryValue", () => {
  it("formats each kind for display", () => {
    expect(formatTelemetryValue({ value: 21.5, valueType: "number" })).toBe(
      "21.5",
    );
    expect(
      formatTelemetryValue({
        value: "auto",
        valueType: "string",
        state: "auto",
      }),
    ).toBe("auto");
    expect(
      formatTelemetryValue({ value: true, valueType: "boolean", state: true }),
    ).toBe("ON");
    expect(
      formatTelemetryValue({
        value: false,
        valueType: "boolean",
        state: false,
      }),
    ).toBe("OFF");
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
