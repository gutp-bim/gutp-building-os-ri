import { afterEach, describe, expect, it, vi } from "vitest";

// Mock the generated Aspida client: the batch endpoint now routes through
// `apiClient().telemetries.query.batch_latest.$post` (#182 review — no bespoke fetch).
const { postMock } = vi.hoisted(() => ({ postMock: vi.fn() }));
vi.mock("@/lib/infra/aspida-client", () => ({
  apiClient: () => ({
    telemetries: { query: { batch_latest: { $post: postMock } } },
  }),
}));

import { latestTelemetryBatch, MAX_BATCH_POINT_IDS } from "./repository";

/** Resolve `$post` with one row per requested id (mirrors the server echoing the posted ids). */
function echoRows({ body }: { body: { pointIds: string[] } }) {
  return Promise.resolve(body.pointIds.map((pointId) => ({ pointId, datetime: null })));
}

afterEach(() => {
  postMock.mockReset();
});

describe("latestTelemetryBatch", () => {
  it("sends a single request when within the server cap", async () => {
    const ids = Array.from({ length: MAX_BATCH_POINT_IDS }, (_, i) => `p${i}`);
    postMock.mockImplementation(echoRows);

    const rows = await latestTelemetryBatch(ids);

    expect(postMock).toHaveBeenCalledOnce();
    expect(rows).toHaveLength(MAX_BATCH_POINT_IDS);
  });

  it("splits an over-cap request into <=500 chunks and merges the results", async () => {
    const ids = Array.from({ length: MAX_BATCH_POINT_IDS + 1 }, (_, i) => `p${i}`);
    const sizes: number[] = [];
    postMock.mockImplementation((option: { body: { pointIds: string[] } }) => {
      sizes.push(option.body.pointIds.length);
      expect(option.body.pointIds.length).toBeLessThanOrEqual(MAX_BATCH_POINT_IDS);
      return echoRows(option);
    });

    const rows = await latestTelemetryBatch(ids);

    expect(postMock).toHaveBeenCalledTimes(2);
    expect(sizes).toEqual([MAX_BATCH_POINT_IDS, 1]);
    expect(rows.map((r) => r.pointId)).toEqual(ids);
  });

  it("rejects when any chunk fails rather than reporting the points as missing", async () => {
    const ids = Array.from({ length: MAX_BATCH_POINT_IDS + 1 }, (_, i) => `p${i}`);
    let call = 0;
    postMock.mockImplementation((option: { body: { pointIds: string[] } }) => {
      call += 1;
      // First chunk fails with an axios-shaped rejection; the whole call must reject.
      return call === 1
        ? Promise.reject({ response: { status: 503 } })
        : echoRows(option);
    });

    await expect(latestTelemetryBatch(ids)).rejects.toThrow(/503/);
  });

  it("returns an empty array without calling the client for no ids", async () => {
    expect(await latestTelemetryBatch([])).toEqual([]);
    expect(postMock).not.toHaveBeenCalled();
  });

  it("drops a non-numeric latest reading to a null value, keeping its lastSeen", async () => {
    postMock.mockResolvedValue([
      { pointId: "p1", datetime: "2026-01-01T01:00:00Z", value: 21.5, valueType: "number" },
      // A non-numeric latest reading must not reach the alarm evaluator as a number (#344: the
      // reading now rides in `value` itself).
      {
        pointId: "p2",
        datetime: "2026-01-01T02:00:00Z",
        value: "auto",
        valueType: "string",
      },
    ]);

    expect(await latestTelemetryBatch(["p1", "p2"])).toEqual([
      { pointId: "p1", lastSeen: "2026-01-01T01:00:00Z", value: 21.5 },
      { pointId: "p2", lastSeen: "2026-01-01T02:00:00Z", value: null },
    ]);
  });
});
