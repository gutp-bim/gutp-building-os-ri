import { describe, expect, it, vi } from "vitest";
import { loadPointsFreshness } from "./freshness-loader";
import type { TelemetryPoint } from "./types";

const NOW = new Date("2026-07-15T12:00:00Z");

function ago(seconds: number): TelemetryPoint {
  return { t: new Date(NOW.getTime() - seconds * 1000).toISOString(), v: 1 };
}

describe("loadPointsFreshness", () => {
  it("classifies each point from its injected latest sample", async () => {
    const latest: Record<string, TelemetryPoint | null> = {
      FRESH: ago(10),
      STALE: ago(1000),
      MISSING: null,
    };
    const results = await loadPointsFreshness(["FRESH", "STALE", "MISSING"], {
      now: NOW,
      thresholdSeconds: 300,
      fetchLatest: (id) => Promise.resolve(latest[id]),
    });
    expect(results.map((r) => [r.pointId, r.status])).toEqual([
      ["FRESH", "fresh"],
      ["STALE", "stale"],
      ["MISSING", "missing"],
    ]);
  });

  it("treats a point whose latest fetch rejects as missing, not a thrown load", async () => {
    const results = await loadPointsFreshness(["OK", "BOOM"], {
      now: NOW,
      thresholdSeconds: 300,
      fetchLatest: (id) =>
        id === "BOOM"
          ? Promise.reject(new Error("network"))
          : Promise.resolve(ago(5)),
    });
    expect(results).toEqual([
      { pointId: "OK", status: "fresh", ageSeconds: 5 },
      { pointId: "BOOM", status: "missing", ageSeconds: null },
    ]);
  });

  it("fetches the latest sample exactly once per point", async () => {
    const fetchLatest = vi.fn((_id: string) => Promise.resolve(ago(5)));
    await loadPointsFreshness(["A", "B", "C"], {
      now: NOW,
      thresholdSeconds: 300,
      fetchLatest,
    });
    expect(fetchLatest).toHaveBeenCalledTimes(3);
    expect(fetchLatest.mock.calls.map((c) => c[0])).toEqual(["A", "B", "C"]);
  });

  it("returns an empty array for no points without calling the fetcher", async () => {
    const fetchLatest = vi.fn();
    expect(
      await loadPointsFreshness([], {
        now: NOW,
        thresholdSeconds: 300,
        fetchLatest,
      }),
    ).toEqual([]);
    expect(fetchLatest).not.toHaveBeenCalled();
  });
});
