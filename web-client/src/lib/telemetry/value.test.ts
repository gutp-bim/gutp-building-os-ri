import { describe, expect, it } from "vitest";
import {
  formatResolvedValue,
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
   * The #152 wire, before #344 made `value` a union: a non-numeric reading arrived with
   * `value: null` and the payload in a field this client no longer reads. It resolves to nothing.
   *
   * Two servers back, not one — worth stating because it is easy to mistake this for the pre-#359
   * shape. The #344 dual-emit server already put the reading in `value` (`Resolve` is
   * `Value ?? ValueText ?? ValueBool`, and a raw non-numeric row has a null `Value`), so it sent
   * `{ value: "auto", valueText: "auto" }` — never this.
   */
  it("does not resolve a pre-#344 server's non-numeric reading (value was still null then)", () => {
    const pre344 = {
      value: null,
      valueType: "string",
      valueText: "auto",
    } as TelemetryWireValue;

    expect(resolveTelemetryValue(pre344)).toEqual({ kind: "none" });
  });
});

/**
 * What actually breaks when the API server and this client straddle #359.
 *
 * **The exposure is asymmetric, and the bad direction is a client newer than its server.**
 * `resolveStateValue` now reads `state` and nothing else, so against a #344 server — which sends no
 * `state` at all — it returns nothing for *every* row shape. `toStateSeries` then yields `[]`, and
 * the state timeline is empty at raw, hour and day alike. A point that only ever emits strings
 * renders neither a chart (`toSeries` drops non-numeric) nor a timeline: a blank screen, no error.
 *
 * The other direction is the narrow one. A #344 client's resolver fell through
 * `valueText → valueBool → value`, and #344 already routed the reading itself through `value`, so
 * against a #359 server a raw non-numeric row and a purely non-numeric bucket (whose `Avg` is null,
 * so `value` carries the representative) both still resolve. Only the mixed aggregate bucket is
 * lost there — `value` is the average, so its state rode in a field that changed names.
 *
 * **So: deploy the API server before the web client.** Both directions are covered below, in that
 * order.
 *
 * Pinned rather than papered over with a runtime fallback: that fallback chain is exactly what #359
 * deleted, and re-adding it would make the removal pointless. The casts are here because TypeScript
 * already rejects the foreign shape, which is the real guard.
 */
describe("#359 wire-version skew", () => {
  // ── The bad direction: this client against a #344 server ────────────────
  //
  // Every one of these is a shape the state timeline renders today. All of them go dark, which is
  // why the ordering constraint is one-directional.
  it.each([
    ["raw string row", { value: "auto", valueType: "string", valueText: "auto" }],
    ["raw boolean row", { value: true, valueType: "boolean", valueBool: true }],
    ["purely non-numeric bucket", { value: "auto", valueType: "string", valueText: "auto" }],
    ["mixed aggregate bucket", { value: 42, valueType: "number", valueText: "auto" }],
  ])("loses the state half of a #344-era %s", (_label, row) => {
    expect(resolveStateValue(row as TelemetryWireValue)).toEqual({ kind: "none" });
  });

  // The chart half is untouched in that pairing — `value` did not change at #359 — so a numeric
  // point looks completely healthy while every state point is blank. That is what makes the
  // failure quiet.
  it("still resolves the reading itself from a #344 server, so only the state half goes dark", () => {
    expect(
      resolveTelemetryValue({
        value: "auto",
        valueType: "string",
        valueText: "auto",
      } as TelemetryWireValue),
    ).toEqual({ kind: "string", value: "auto" });

    expect(
      resolveTelemetryValue({
        value: 42,
        valueType: "number",
        valueText: "auto",
      } as TelemetryWireValue),
    ).toEqual({ kind: "number", value: 42 });
  });

  // ── The narrow direction: a #344 client against this server ─────────────
  //
  // Not reproducible from here (it is the old resolver that matters), so it is recorded rather than
  // executed: the old fallback ended at `value`, which carries the reading for a raw row and for a
  // purely non-numeric bucket, but is the average for a mixed one. Mixed buckets only.
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
