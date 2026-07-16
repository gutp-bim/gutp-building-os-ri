import { afterEach, describe, expect, it, vi } from "vitest";
import { latestTelemetryBatch, MAX_BATCH_POINT_IDS } from "./repository";

// A stubbed global fetch that records the request bodies and returns one row per requested id.
function stubFetch(
  impl: (url: string, init: RequestInit) => Response | Promise<Response>,
) {
  const spy = vi.fn(impl);
  vi.stubGlobal("fetch", spy as unknown as typeof fetch);
  return spy;
}

function okRows(pointIds: string[]): Response {
  const body = pointIds.map((pointId) => ({ pointId, datetime: null }));
  return new Response(JSON.stringify(body), { status: 200 });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("latestTelemetryBatch", () => {
  it("sends a single request when within the server cap", async () => {
    const ids = Array.from({ length: MAX_BATCH_POINT_IDS }, (_, i) => `p${i}`);
    const spy = stubFetch((_url, init) => {
      const { pointIds } = JSON.parse(String(init.body)) as { pointIds: string[] };
      return okRows(pointIds);
    });

    const rows = await latestTelemetryBatch(ids);

    expect(spy).toHaveBeenCalledOnce();
    expect(rows).toHaveLength(MAX_BATCH_POINT_IDS);
  });

  it("splits an over-cap request into <=500 chunks and merges the results", async () => {
    const ids = Array.from({ length: MAX_BATCH_POINT_IDS + 1 }, (_, i) => `p${i}`);
    const sizes: number[] = [];
    const spy = stubFetch((_url, init) => {
      const { pointIds } = JSON.parse(String(init.body)) as { pointIds: string[] };
      sizes.push(pointIds.length);
      expect(pointIds.length).toBeLessThanOrEqual(MAX_BATCH_POINT_IDS);
      return okRows(pointIds);
    });

    const rows = await latestTelemetryBatch(ids);

    expect(spy).toHaveBeenCalledTimes(2);
    expect(sizes).toEqual([MAX_BATCH_POINT_IDS, 1]);
    expect(rows.map((r) => r.pointId)).toEqual(ids);
  });

  it("rejects when any chunk fails rather than reporting the points as missing", async () => {
    const ids = Array.from({ length: MAX_BATCH_POINT_IDS + 1 }, (_, i) => `p${i}`);
    let call = 0;
    stubFetch((_url, init) => {
      const { pointIds } = JSON.parse(String(init.body)) as { pointIds: string[] };
      call += 1;
      return call === 1
        ? new Response("boom", { status: 503 })
        : okRows(pointIds);
    });

    await expect(latestTelemetryBatch(ids)).rejects.toThrow(/503/);
  });

  it("returns an empty array without calling fetch for no ids", async () => {
    const spy = stubFetch(() => okRows([]));
    expect(await latestTelemetryBatch([])).toEqual([]);
    expect(spy).not.toHaveBeenCalled();
  });
});
