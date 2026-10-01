import { afterEach, describe, expect, it, vi } from "vitest";

const { getMock, coverageMock } = vi.hoisted(() => ({
  getMock: vi.fn(),
  coverageMock: vi.fn(),
}));
vi.mock("@/lib/infra/aspida-client", () => ({
  apiClient: () => ({
    api: {
      v1: {
        telemetries: {
          query: { get: getMock },
          coverage: { get: coverageMock },
        },
      },
    },
  }),
}));

import { COVERAGE_BUCKET_COUNT } from "./coverage";
import { queryPointCoverage } from "./repository";

const END = new Date("2026-09-10T12:00:00Z");

/** aspida's full response (`get`, not `$get`) — the partial-result header lives on it (#499). */
const ok = (body: unknown, headers: Record<string, string> = {}) => ({
  status: 200,
  headers,
  body,
});

afterEach(() => {
  getMock.mockReset();
  coverageMock.mockReset();
});

describe("queryPointCoverage (#457)", () => {
  it("fetches raw receipts for the planned window and returns 96 buckets", async () => {
    getMock.mockResolvedValue(
      ok([
        { datetime: "2026-09-10T11:50:00Z", value: 1, valueType: "number" },
        { datetime: "2026-09-10T11:51:00Z", value: "ON", valueType: "string" },
        { datetime: null, value: 2 },
      ]),
    );

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

  it("asks the server to count a fast point instead of fetching its raw rows (#551)", async () => {
    const counts = Array.from({ length: COVERAGE_BUCKET_COUNT }, () => 180);
    counts[95] = 0;
    coverageMock.mockResolvedValue(
      ok({
        pointId: "p1",
        windowStart: "2026-09-09T12:00:00Z",
        windowEnd: END.toISOString(),
        bucketSeconds: 900,
        counts,
      }),
    );

    const result = await queryPointCoverage({
      pointId: "p1",
      intervalSeconds: 5,
      windowEnd: END,
    });

    expect(getMock).not.toHaveBeenCalled();
    expect(coverageMock).toHaveBeenCalledWith({
      query: { pointId: "p1", end: END.toISOString() },
    });
    expect(result.kind).toBe("buckets");
    if (result.kind !== "buckets") return;
    expect(result.buckets).toHaveLength(COVERAGE_BUCKET_COUNT);
    expect(result.buckets[0].level).toBe("full");
    expect(result.buckets.at(-1)!.level).toBe("none");
  });

  it("rejects a malformed server response rather than drawing a wrong bar", async () => {
    coverageMock.mockResolvedValue(
      ok({ bucketSeconds: 900, counts: [1, 2, 3] }),
    );

    await expect(
      queryPointCoverage({ pointId: "p1", intervalSeconds: 5, windowEnd: END }),
    ).rejects.toThrow();
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

  // #499: a read capped by PARQUET_QUERY_MAX_FILES returns fewer rows; drawn as-is it would look
  // like missing data. Both paths report "partial" instead.
  it("does not draw a capped server count as missing data", async () => {
    coverageMock.mockResolvedValue(
      ok(
        {
          windowStart: "2026-09-09T12:00:00Z",
          bucketSeconds: 900,
          counts: Array.from({ length: COVERAGE_BUCKET_COUNT }, () => 0),
        },
        {
          "x-partial-result": "true",
          "x-covered-from": "2026-09-10T06:00:00Z",
        },
      ),
    );

    expect(
      await queryPointCoverage({
        pointId: "p1",
        intervalSeconds: 5,
        windowEnd: END,
      }),
    ).toEqual({ kind: "unavailable", reason: "partial" });
  });

  it("does not draw a capped raw read as missing data", async () => {
    getMock.mockResolvedValue(ok([], { "X-Partial-Result": "true" }));

    expect(
      await queryPointCoverage({
        pointId: "p1",
        intervalSeconds: 60,
        windowEnd: END,
      }),
    ).toEqual({ kind: "unavailable", reason: "partial" });
  });
});
