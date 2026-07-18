import { describe, expect, it } from "vitest";
import {
  DEFAULT_STALE_THRESHOLD_SECONDS,
  classifyPointFreshness,
  summarizeFreshness,
} from "./freshness";
import type { PointFreshness, PointLastSeen } from "./freshness";

// A fixed clock so the pure classifier is deterministic (no Date.now() inside the function).
const NOW = new Date("2026-07-15T12:00:00Z");

/** lastSeen = NOW minus `seconds`, as an ISO string. */
function ago(seconds: number): string {
  return new Date(NOW.getTime() - seconds * 1000).toISOString();
}

describe("classifyPointFreshness", () => {
  it("classifies a point with no telemetry sample as missing", () => {
    const points: PointLastSeen[] = [{ pointId: "PT001", lastSeen: null }];
    const [r] = classifyPointFreshness(points, NOW, 300);
    expect(r.pointId).toBe("PT001");
    expect(r.status).toBe("missing");
    expect(r.ageSeconds).toBeNull();
  });

  it("classifies a just-received sample as fresh with age ~0", () => {
    const [r] = classifyPointFreshness(
      [{ pointId: "PT001", lastSeen: ago(0) }],
      NOW,
      300,
    );
    expect(r.status).toBe("fresh");
    expect(r.ageSeconds).toBe(0);
  });

  it("treats a sample exactly at the threshold as fresh (boundary is inclusive)", () => {
    const [r] = classifyPointFreshness(
      [{ pointId: "PT001", lastSeen: ago(300) }],
      NOW,
      300,
    );
    expect(r.status).toBe("fresh");
    expect(r.ageSeconds).toBe(300);
  });

  it("classifies a sample one second past the threshold as stale", () => {
    const [r] = classifyPointFreshness(
      [{ pointId: "PT001", lastSeen: ago(301) }],
      NOW,
      300,
    );
    expect(r.status).toBe("stale");
    expect(r.ageSeconds).toBe(301);
  });

  it("reports the integer age in seconds, flooring sub-second remainders", () => {
    const lastSeen = new Date(NOW.getTime() - 42_800).toISOString(); // 42.8s ago
    const [r] = classifyPointFreshness(
      [{ pointId: "PT001", lastSeen }],
      NOW,
      300,
    );
    expect(r.ageSeconds).toBe(42);
  });

  it("treats a future timestamp (clock skew) as fresh, not stale", () => {
    const [r] = classifyPointFreshness(
      [{ pointId: "PT001", lastSeen: ago(-30) }], // 30s in the future
      NOW,
      300,
    );
    expect(r.status).toBe("fresh");
  });

  it("classifies an unparseable lastSeen as missing (defensive)", () => {
    const [r] = classifyPointFreshness(
      [{ pointId: "PT001", lastSeen: "not-a-date" }],
      NOW,
      300,
    );
    expect(r.status).toBe("missing");
    expect(r.ageSeconds).toBeNull();
  });

  it("preserves input order and classifies each point independently", () => {
    const points: PointLastSeen[] = [
      { pointId: "FRESH", lastSeen: ago(10) },
      { pointId: "STALE", lastSeen: ago(1000) },
      { pointId: "MISSING", lastSeen: null },
    ];
    const results = classifyPointFreshness(points, NOW, 300);
    expect(results.map((r) => [r.pointId, r.status])).toEqual([
      ["FRESH", "fresh"],
      ["STALE", "stale"],
      ["MISSING", "missing"],
    ]);
  });

  it("returns an empty array for empty input", () => {
    expect(classifyPointFreshness([], NOW, 300)).toEqual([]);
  });

  it("respects a caller-supplied threshold other than the default", () => {
    // With a 60s threshold, a 120s-old sample is stale even though it would be fresh at 300s.
    const [r] = classifyPointFreshness(
      [{ pointId: "PT001", lastSeen: ago(120) }],
      NOW,
      60,
    );
    expect(r.status).toBe("stale");
  });

  it("exposes the backend registry default (300s) as a shared constant", () => {
    expect(DEFAULT_STALE_THRESHOLD_SECONDS).toBe(300);
  });

  it("uses a point's own thresholdSeconds over the default when provided (#183)", () => {
    // A fast point (expected 5s → threshold 15s) is stale at 120s even though the 300s default
    // would still call it fresh; a slow point (expected 1d) stays fresh at the same age.
    const points: PointLastSeen[] = [
      { pointId: "FAST", lastSeen: ago(120), thresholdSeconds: 15 },
      { pointId: "SLOW", lastSeen: ago(120), thresholdSeconds: 86_400 },
    ];
    const results = classifyPointFreshness(points, NOW, 300);
    expect(results.map((r) => [r.pointId, r.status])).toEqual([
      ["FAST", "stale"],
      ["SLOW", "fresh"],
    ]);
  });

  it("falls back to the default threshold for points without their own thresholdSeconds", () => {
    const points: PointLastSeen[] = [
      { pointId: "OVERRIDE", lastSeen: ago(120), thresholdSeconds: 60 },
      { pointId: "DEFAULT", lastSeen: ago(120) },
    ];
    const results = classifyPointFreshness(points, NOW, 300);
    expect(results.map((r) => [r.pointId, r.status])).toEqual([
      ["OVERRIDE", "stale"],
      ["DEFAULT", "fresh"],
    ]);
  });
});

describe("summarizeFreshness", () => {
  it("counts each status and the total", () => {
    const results: PointFreshness[] = [
      { pointId: "A", status: "fresh", ageSeconds: 1 },
      { pointId: "B", status: "fresh", ageSeconds: 2 },
      { pointId: "C", status: "stale", ageSeconds: 900 },
      { pointId: "D", status: "missing", ageSeconds: null },
    ];
    expect(summarizeFreshness(results)).toEqual({
      fresh: 2,
      stale: 1,
      missing: 1,
      total: 4,
    });
  });

  it("returns all-zero counts for an empty result set", () => {
    expect(summarizeFreshness([])).toEqual({
      fresh: 0,
      stale: 0,
      missing: 0,
      total: 0,
    });
  });
});
