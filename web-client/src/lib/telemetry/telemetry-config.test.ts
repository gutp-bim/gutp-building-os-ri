import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { classifyPointFreshness } from "./freshness";
import { resolveStaleThresholdSeconds } from "./freshness-threshold";
import { getTelemetryConfig, resetTelemetryConfigCache } from "./repository";

function mockFetchJson(body: unknown, ok = true) {
  global.fetch = vi
    .fn()
    .mockResolvedValue({ ok, json: async () => body }) as unknown as typeof fetch;
}

beforeEach(() => resetTelemetryConfigCache());
afterEach(() => vi.restoreAllMocks());

describe("getTelemetryConfig (#183)", () => {
  it("returns the effective thresholds served by /api/telemetry/config", async () => {
    mockFetchJson({ staleThresholdSeconds: 600, staleIntervalMultiplier: 5 });
    expect(await getTelemetryConfig()).toEqual({
      staleThresholdSeconds: 600,
      staleIntervalMultiplier: 5,
    });
  });

  it("falls back to the defaults when the endpoint fails (freshness never breaks)", async () => {
    global.fetch = vi
      .fn()
      .mockRejectedValue(new Error("down")) as unknown as typeof fetch;
    expect(await getTelemetryConfig()).toEqual({
      staleThresholdSeconds: 300,
      staleIntervalMultiplier: 3,
    });
  });

  it("caches the config so repeated calls fetch once", async () => {
    mockFetchJson({ staleThresholdSeconds: 300, staleIntervalMultiplier: 3 });
    await getTelemetryConfig();
    await getTelemetryConfig();
    expect(global.fetch).toHaveBeenCalledTimes(1);
  });

  // The #210-review regression: changing the multiplier through the actual runtime supply path
  // (fetch → config → threshold → classification) flips a point stale→fresh for the same interval+age.
  it("flips a point stale→fresh when the served multiplier goes 3→5", async () => {
    const now = new Date("2026-07-18T00:04:00Z"); // 240s after the last sample
    const lastSeen = "2026-07-18T00:00:00Z";

    const statusFor = async (multiplier: number) => {
      resetTelemetryConfigCache();
      mockFetchJson({ staleThresholdSeconds: 300, staleIntervalMultiplier: multiplier });
      const cfg = await getTelemetryConfig();
      const thresholdSeconds = resolveStaleThresholdSeconds({
        expected: { point: 60 },
        multiplier: cfg.staleIntervalMultiplier,
        systemDefaultThresholdSeconds: cfg.staleThresholdSeconds,
      });
      return classifyPointFreshness([{ pointId: "p", lastSeen }], now, thresholdSeconds)[0].status;
    };

    expect(await statusFor(3)).toBe("stale"); // 60×3 = 180 < 240
    expect(await statusFor(5)).toBe("fresh"); // 60×5 = 300 ≥ 240
  });
});
