import { afterEach, describe, expect, it, vi } from "vitest";

const { getMock } = vi.hoisted(() => ({ getMock: vi.fn() }));
vi.mock("@/lib/infra/aspida-client", () => ({
  apiClient: () => ({
    api: { v1: { telemetries: { query: { $get: getMock } } } },
  }),
}));

import { COVERAGE_BUCKET_COUNT } from "./coverage";
import { queryPointCoverage } from "./repository";

const END = new Date("2026-09-10T12:00:00Z");

afterEach(() => {
  getMock.mockReset();
});

describe("queryPointCoverage (#457)", () => {
  it("fetches raw receipts for the planned window and returns 96 buckets", async () => {
    getMock.mockResolvedValue([
      { datetime: "2026-09-10T11:50:00Z", value: 1, valueType: "number" },
      { datetime: "2026-09-10T11:51:00Z", value: "ON", valueType: "string" },
      { datetime: null, value: 2 },
    ]);

    const result = await queryPointCoverage({
      pointId: "p1",
      intervalSeconds: 60,
      windowEnd: END,
    });

    expect(getMock).toHaveBeenCalledOnce();
    const query = getMock.mock.calls[0][0].query;
    expect(query.pointId).toBe("p1");
    expect(query.start).toBe("2026-09-09T12:00:00.000Z");
    expect(query.end).toBe(END.toISOString());
    // raw で取得する（粒度 raw = 0）
    expect(query.granularity).toBe(0);

    expect(result.kind).toBe("buckets");
    if (result.kind !== "buckets") return;
    expect(result.buckets).toHaveLength(COVERAGE_BUCKET_COUNT);
    // 数値・非数値どちらの受信も数える（受信状況は値の種類を問わない）
    expect(result.buckets.at(-1)!.received).toBe(2);
  });

  it("does not hit the API for a fast point", async () => {
    const result = await queryPointCoverage({
      pointId: "p1",
      intervalSeconds: 5,
      windowEnd: END,
    });

    expect(getMock).not.toHaveBeenCalled();
    expect(result).toEqual({ kind: "unavailable", reason: "too-dense" });
  });

  it("does not hit the API when the point has no expected interval", async () => {
    const result = await queryPointCoverage({
      pointId: "p1",
      intervalSeconds: null,
      windowEnd: END,
    });

    expect(getMock).not.toHaveBeenCalled();
    expect(result).toEqual({ kind: "unavailable", reason: "no-interval" });
  });
});
