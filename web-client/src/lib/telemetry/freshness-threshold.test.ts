import { describe, expect, it } from "vitest";
import {
  DEFAULT_STALE_INTERVAL_MULTIPLIER,
  resolveExpectedIntervalSeconds,
  resolveStaleThresholdSeconds,
} from "./freshness-threshold";

describe("resolveExpectedIntervalSeconds", () => {
  it("prefers the point-specific interval over device/gateway", () => {
    expect(
      resolveExpectedIntervalSeconds({ point: 60, device: 120, gateway: 300 }),
    ).toBe(60);
  });

  it("falls back point → device → gateway in order", () => {
    expect(resolveExpectedIntervalSeconds({ device: 120, gateway: 300 })).toBe(
      120,
    );
    expect(resolveExpectedIntervalSeconds({ gateway: 300 })).toBe(300);
  });

  it("returns null when no tier supplies a usable interval", () => {
    expect(resolveExpectedIntervalSeconds({})).toBeNull();
    expect(
      resolveExpectedIntervalSeconds({ point: null, device: undefined }),
    ).toBeNull();
  });

  it("ignores non-positive, NaN, or non-finite intervals and continues down the hierarchy", () => {
    // A zero/negative expected interval is meaningless (would make everything stale); skip it.
    expect(resolveExpectedIntervalSeconds({ point: 0, device: 90 })).toBe(90);
    expect(resolveExpectedIntervalSeconds({ point: -5, gateway: 30 })).toBe(30);
    expect(
      resolveExpectedIntervalSeconds({ point: Number.NaN, device: 45 }),
    ).toBe(45);
    expect(
      resolveExpectedIntervalSeconds({ point: Number.POSITIVE_INFINITY }),
    ).toBeNull();
  });
});

describe("resolveStaleThresholdSeconds", () => {
  it("multiplies the resolved expected interval by N (default 3)", () => {
    expect(
      resolveStaleThresholdSeconds({
        expected: { point: 60 },
        systemDefaultThresholdSeconds: 300,
      }),
    ).toBe(180);
  });

  it("honors a custom multiplier", () => {
    expect(
      resolveStaleThresholdSeconds({
        expected: { point: 30 },
        multiplier: 5,
        systemDefaultThresholdSeconds: 300,
      }),
    ).toBe(150);
  });

  it("falls back to the system default threshold when no expected interval is known", () => {
    // No point/device/gateway interval → keep today's behaviour (the 300s registry default),
    // NOT default×multiplier — the multiplier only applies to a real expected interval.
    expect(
      resolveStaleThresholdSeconds({
        expected: {},
        systemDefaultThresholdSeconds: 300,
      }),
    ).toBe(300);
  });

  it("uses the resolved tier (device) when point is absent", () => {
    expect(
      resolveStaleThresholdSeconds({
        expected: { device: 600 },
        systemDefaultThresholdSeconds: 300,
      }),
    ).toBe(1800);
  });

  it("guards against a non-positive multiplier by reverting to the default", () => {
    expect(
      resolveStaleThresholdSeconds({
        expected: { point: 60 },
        multiplier: 0,
        systemDefaultThresholdSeconds: 300,
      }),
    ).toBe(60 * DEFAULT_STALE_INTERVAL_MULTIPLIER);
  });

  it("exposes the default multiplier constant (3), mirroring the registry default", () => {
    expect(DEFAULT_STALE_INTERVAL_MULTIPLIER).toBe(3);
  });
});
