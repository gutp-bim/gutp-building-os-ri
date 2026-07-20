import { describe, expect, it, vi } from "vitest";
import type { AlarmThresholds } from "./alarm";
import { loadPointsAlarms } from "./alarm-loader";
import type { PointLastSeen } from "./freshness";

describe("loadPointsAlarms (#158 Phase 2a)", () => {
  it("classifies each point's current value against its thresholds", async () => {
    const rows: PointLastSeen[] = [
      { pointId: "a", lastSeen: "t", value: 31 },
      { pointId: "b", lastSeen: "t", value: 20 },
      { pointId: "c", lastSeen: "t", value: 50 }, // no thresholds → unknown
    ];
    const thresholds = new Map<string, AlarmThresholds | undefined>([
      ["a", { alarmHigh: 30 }],
      ["b", { alarmHigh: 30 }],
    ]);
    const fetch = vi.fn().mockResolvedValue(rows);

    const result = await loadPointsAlarms(["a", "b", "c"], thresholds, fetch);

    expect(result.find((r) => r.pointId === "a")?.status).toBe("critical");
    expect(result.find((r) => r.pointId === "b")?.status).toBe("ok");
    expect(result.find((r) => r.pointId === "c")?.status).toBe("unknown");
    expect(fetch).toHaveBeenCalledOnce();
  });

  it("treats a point missing from the batch as no value (unknown)", async () => {
    const fetch = vi.fn().mockResolvedValue([]); // batch omitted the point (no read perm)
    const result = await loadPointsAlarms(
      ["a"],
      new Map([["a", { alarmHigh: 30 }]]),
      fetch,
    );
    expect(result[0]?.status).toBe("unknown");
  });

  it("short-circuits an empty point list without fetching", async () => {
    const fetch = vi.fn();
    expect(await loadPointsAlarms([], new Map(), fetch)).toEqual([]);
    expect(fetch).not.toHaveBeenCalled();
  });

  it("rethrows a failed fetch (so the caller can show an error)", async () => {
    const fetch = vi.fn().mockRejectedValue(new Error("最新値の一括取得に失敗しました"));
    await expect(
      loadPointsAlarms(["a"], new Map([["a", { alarmHigh: 30 }]]), fetch),
    ).rejects.toThrow("最新値の一括取得に失敗しました");
  });
});
