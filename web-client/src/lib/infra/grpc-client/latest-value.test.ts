import { describe, expect, it } from "vitest";
import {
  MAX_BACKOFF_MS,
  backoffMs,
  isFatalCode,
  isNewer,
  toLatestSample,
  type LatestValueEventLike,
} from "./latest-value";

const ev = (
  value: LatestValueEventLike["value"],
  datetime = "2026-11-08T00:00:00Z",
): LatestValueEventLike => ({
  pointId: "P1",
  value,
  datetime,
  snapshot: false,
});

describe("toLatestSample (#636)", () => {
  it("maps each oneof case to the resolved value the REST read produces", () => {
    expect(toLatestSample(ev({ case: "numberValue", value: 21.5 }))).toEqual({
      t: "2026-11-08T00:00:00Z",
      value: { kind: "number", value: 21.5 },
    });
    expect(
      toLatestSample(ev({ case: "stringValue", value: "on" })).value,
    ).toEqual({ kind: "string", value: "on" });
    expect(
      toLatestSample(ev({ case: "boolValue", value: false })).value,
    ).toEqual({ kind: "boolean", value: false });
  });

  it("an unset value is none, and an empty datetime is null", () => {
    expect(toLatestSample(ev({ case: undefined }, ""))).toEqual({
      t: null,
      value: { kind: "none" },
    });
  });
});

describe("isNewer", () => {
  const at = (t: string | null) => ({
    t,
    value: { kind: "number" as const, value: 1 },
  });

  it("replaces nothing-yet, and only a later reading replaces the current one", () => {
    expect(isNewer(at("2026-11-08T00:00:00Z"), null)).toBe(true);
    expect(
      isNewer(at("2026-11-08T00:00:10Z"), at("2026-11-08T00:00:00Z")),
    ).toBe(true);
    expect(
      isNewer(at("2026-11-08T00:00:00Z"), at("2026-11-08T00:00:10Z")),
    ).toBe(false);
    expect(
      isNewer(at("2026-11-08T00:00:00Z"), at("2026-11-08T00:00:00Z")),
    ).toBe(false);
  });

  it("a different value at the same time wins (later arrival), the same reading does not", () => {
    const one = {
      t: "2026-11-08T00:00:00Z",
      value: { kind: "number" as const, value: 1 },
    };
    const two = {
      t: "2026-11-08T00:00:00Z",
      value: { kind: "number" as const, value: 2 },
    };
    expect(isNewer(two, one)).toBe(true);
    expect(isNewer({ ...one }, one)).toBe(false);
    expect(
      isNewer({ t: one.t, value: { kind: "string", value: "on" } }, one),
    ).toBe(true);
  });

  it("a sample without a time never replaces one with a time", () => {
    expect(isNewer(at(null), at("2026-11-08T00:00:00Z"))).toBe(false);
    expect(isNewer(at("2026-11-08T00:00:00Z"), at(null))).toBe(true);
  });
});

describe("reconnect policy", () => {
  it("backs off exponentially from 1s up to the cap", () => {
    expect([1, 2, 3, 4].map(backoffMs)).toEqual([1000, 2000, 4000, 8000]);
    expect(backoffMs(20)).toBe(MAX_BACKOFF_MS);
    expect(backoffMs(0)).toBe(1000);
  });

  it("gives up only on codes a retry cannot fix", () => {
    // Unauthenticated, PermissionDenied, InvalidArgument, Unimplemented
    expect([16, 7, 3, 12].every(isFatalCode)).toBe(true);
    // Unavailable, Unknown, DeadlineExceeded, no code (network)
    expect([14, 2, 4, undefined].some(isFatalCode)).toBe(false);
  });
});
