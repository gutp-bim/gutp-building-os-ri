import { describe, expect, it, vi } from "vitest";
import type { PointLastSeen } from "./freshness";
import { loadPointsFreshness } from "./freshness-loader";

const NOW = new Date("2026-07-15T12:00:00Z");

/** ISO timestamp `seconds` before NOW. */
function ago(seconds: number): string {
  return new Date(NOW.getTime() - seconds * 1000).toISOString();
}

describe("loadPointsFreshness", () => {
  it("classifies each point from the batch latest samples", async () => {
    const fetchLatestBatch = vi.fn().mockResolvedValue([
      { pointId: "FRESH", lastSeen: ago(10) },
      { pointId: "STALE", lastSeen: ago(1000) },
      { pointId: "MISSING", lastSeen: null },
    ] satisfies PointLastSeen[]);

    const results = await loadPointsFreshness(["FRESH", "STALE", "MISSING"], {
      now: NOW,
      thresholdSeconds: 300,
      fetchLatestBatch,
    });

    expect(results.map((r) => [r.pointId, r.status])).toEqual([
      ["FRESH", "fresh"],
      ["STALE", "stale"],
      ["MISSING", "missing"],
    ]);
  });

  it("fills a point the batch omits as missing (order follows the request)", async () => {
    // The server drops points a non-admin cannot read; the caller must still account for them.
    const fetchLatestBatch = vi
      .fn()
      .mockResolvedValue([{ pointId: "B", lastSeen: ago(5) }] satisfies PointLastSeen[]);

    const results = await loadPointsFreshness(["A", "B"], {
      now: NOW,
      thresholdSeconds: 300,
      fetchLatestBatch,
    });

    expect(results).toEqual([
      { pointId: "A", status: "missing", ageSeconds: null },
      { pointId: "B", status: "fresh", ageSeconds: 5 },
    ]);
  });

  it("propagates a batch fetch failure instead of masking it as all-missing", async () => {
    // A transient fetch failure must be distinguishable from "the points genuinely have no data":
    // the loader rethrows so the operator home renders its error banner rather than silently
    // showing every point as 欠測 (missing). #182 review point 1.
    await expect(
      loadPointsFreshness(["A", "B"], {
        now: NOW,
        thresholdSeconds: 300,
        fetchLatestBatch: () => Promise.reject(new Error("network")),
      }),
    ).rejects.toThrow("network");
  });

  it("makes exactly one batch call for all points", async () => {
    const fetchLatestBatch = vi.fn().mockResolvedValue([]);
    await loadPointsFreshness(["A", "B", "C"], {
      now: NOW,
      thresholdSeconds: 300,
      fetchLatestBatch,
    });
    expect(fetchLatestBatch).toHaveBeenCalledTimes(1);
    expect(fetchLatestBatch).toHaveBeenCalledWith(["A", "B", "C"]);
  });

  it("derives per-point thresholds from expected intervals × multiplier (#183)", async () => {
    // FAST expects data every 5s (threshold 5×3=15s) → 120s old is stale.
    // SLOW expects data every 1h (threshold 3600×3=10800s) → 120s old is still fresh.
    // NOINT has no expected interval → the system default (300s) applies → 120s old is fresh.
    const fetchLatestBatch = vi.fn().mockResolvedValue([
      { pointId: "FAST", lastSeen: ago(120) },
      { pointId: "SLOW", lastSeen: ago(120) },
      { pointId: "NOINT", lastSeen: ago(120) },
    ] satisfies PointLastSeen[]);

    const results = await loadPointsFreshness(["FAST", "SLOW", "NOINT"], {
      now: NOW,
      thresholdSeconds: 300,
      intervalMultiplier: 3,
      expectedIntervalSeconds: new Map([
        ["FAST", 5],
        ["SLOW", 3600],
      ]),
      fetchLatestBatch,
    });

    expect(results.map((r) => [r.pointId, r.status])).toEqual([
      ["FAST", "stale"],
      ["SLOW", "fresh"],
      ["NOINT", "fresh"],
    ]);
  });

  it("returns an empty array for no points without calling the fetcher", async () => {
    const fetchLatestBatch = vi.fn();
    expect(
      await loadPointsFreshness([], {
        now: NOW,
        thresholdSeconds: 300,
        fetchLatestBatch,
      }),
    ).toEqual([]);
    expect(fetchLatestBatch).not.toHaveBeenCalled();
  });
});
