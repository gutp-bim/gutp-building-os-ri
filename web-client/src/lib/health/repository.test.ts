import { afterEach, describe, expect, it, vi } from "vitest";

// 生成された aspida クライアントをモックする。呼び出し経路は
// `apiClient(token).api.telemetry.health.$get` / `....health.summary.$get`。
const { listGet, summaryGet, seenTokens } = vi.hoisted(() => ({
  listGet: vi.fn(),
  summaryGet: vi.fn(),
  seenTokens: [] as (string | undefined)[],
}));

vi.mock("@/lib/infra/aspida-client", () => ({
  apiClient: (token?: string) => {
    seenTokens.push(token);
    return {
      api: {
        telemetry: {
          health: { $get: listGet, summary: { $get: summaryGet } },
        },
      },
    };
  },
}));

import { DEFAULT_HEALTH_QUERY, type HealthQuery } from "./query";
import { fetchPointHealth, fetchPointHealthSummary } from "./repository";

/** サーバが返す 1 行（3 軸はネストしていて、enum は PascalCase）。 */
const wireItem = {
  pointId: "PT001",
  pointDtId: "dtmi:pt001",
  name: "給気温度",
  unit: "degC",
  freshness: {
    status: "Stale",
    lastSeen: "2026-09-10T10:23:42Z",
    ageSeconds: 1080,
    expectedIntervalSeconds: 300,
    thresholdSeconds: 900,
    thresholdSource: "Point",
    reason: null,
  },
  alarm: { status: "Suppressed", value: 23.4, violated: null },
  gateway: { id: "GW-001", connected: true },
  healthStatus: "Stale",
  deviceDtId: "dtmi:ahu01",
  deviceName: "AHU-01",
  spaceDtId: "dtmi:room",
  spaceName: "会議室A",
  floorDtId: "dtmi:f1",
  floorName: "1F",
  buildingDtId: "dtmi:b1",
  buildingName: "本館",
  tags: ["hvac"],
};

const wirePage = {
  items: [wireItem],
  total: 1234,
  limit: 100,
  offset: 0,
  dataComplete: true,
  indexState: "Ready",
};

afterEach(() => {
  listGet.mockReset();
  summaryGet.mockReset();
  seenTokens.length = 0;
});

describe("fetchPointHealth", () => {
  it("既定条件では並び順とページングだけを送る（空の絞り込みは送らない）", async () => {
    listGet.mockResolvedValue(wirePage);

    await fetchPointHealth(DEFAULT_HEALTH_QUERY);

    expect(listGet).toHaveBeenCalledWith({
      query: { sort: "worst", limit: 100, offset: 0 },
    });
  });

  it("指定された絞り込みをすべて渡す（多値はそのまま配列で送る）", async () => {
    listGet.mockResolvedValue(wirePage);
    const query: HealthQuery = {
      ...DEFAULT_HEALTH_QUERY,
      freshness: ["stale", "missing"],
      alarm: ["warn", "critical"],
      healthStatus: ["critical"],
      olderThanSeconds: 600,
      buildingDtId: "dtmi:b1",
      floorDtId: "dtmi:f1",
      deviceDtId: "dtmi:ahu01",
      gatewayId: "GW-001",
      tags: ["hvac", "室温"],
      q: "給気",
      sort: "lastSeen",
      limit: 50,
      offset: 100,
    };

    await fetchPointHealth(query);

    expect(listGet).toHaveBeenCalledWith({
      query: {
        buildingDtId: "dtmi:b1",
        floorDtId: "dtmi:f1",
        deviceDtId: "dtmi:ahu01",
        gatewayId: "GW-001",
        freshness: ["stale", "missing"],
        alarm: ["warn", "critical"],
        healthStatus: ["critical"],
        olderThan: 600,
        tag: ["hvac", "室温"],
        q: "給気",
        sort: "lastSeen",
        limit: 50,
        offset: 100,
      },
    });
  });

  it("壊れた条件（範囲外の limit・負の offset）は送る前に正規化する", async () => {
    listGet.mockResolvedValue(wirePage);

    await fetchPointHealth({
      ...DEFAULT_HEALTH_QUERY,
      limit: 9999,
      offset: -5,
      q: "  ",
    });

    expect(listGet).toHaveBeenCalledWith({
      query: { sort: "worst", limit: 500, offset: 0 },
    });
  });

  it("ネストした wire を平らにして 1 行のドメイン型に写す", async () => {
    listGet.mockResolvedValue(wirePage);

    const page = await fetchPointHealth(DEFAULT_HEALTH_QUERY);

    expect(page.total).toBe(1234);
    expect(page.limit).toBe(100);
    expect(page.offset).toBe(0);
    expect(page.dataComplete).toBe(true);
    expect(page.indexState).toBe("ready");
    expect(page.rows).toHaveLength(1);
    expect(page.rows[0]).toMatchObject({
      pointId: "PT001",
      name: "給気温度",
      unit: "degC",
      freshnessStatus: "stale",
      alarmStatus: "suppressed",
      healthStatus: "stale",
      lastSeen: "2026-09-10T10:23:42Z",
      ageSeconds: 1080,
      expectedIntervalSeconds: 300,
      thresholdSeconds: 900,
      thresholdSource: "point",
      value: 23.4,
      gatewayId: "GW-001",
      gatewayConnected: true,
      deviceName: "AHU-01",
      buildingName: "本館",
      tags: ["hvac"],
    });
  });

  it("欠測行の理由（freshness.reason）を missingReason に写す", async () => {
    listGet.mockResolvedValue({
      ...wirePage,
      items: [
        {
          ...wireItem,
          freshness: {
            ...wireItem.freshness,
            status: "Missing",
            lastSeen: null,
            ageSeconds: null,
            reason: "GatewayDisconnected",
          },
        },
      ],
    });

    const page = await fetchPointHealth(DEFAULT_HEALTH_QUERY);

    expect(page.rows[0].freshnessStatus).toBe("missing");
    expect(page.rows[0].missingReason).toBe("gatewayDisconnected");
  });

  it("3 軸のオブジェクトが丸ごと欠けていても既定値で埋める", async () => {
    listGet.mockResolvedValue({ items: [{ pointId: "PT002" }] });

    const page = await fetchPointHealth(DEFAULT_HEALTH_QUERY);

    expect(page.rows[0]).toMatchObject({
      pointId: "PT002",
      name: "PT002",
      freshnessStatus: "unknown",
      alarmStatus: "unknown",
      healthStatus: "unknown",
      gatewayId: null,
      gatewayConnected: null,
    });
  });

  it("pointId を読めない行は落とす（リンク先の無い行を出さない）", async () => {
    listGet.mockResolvedValue({
      items: [{ ...wireItem, pointId: "" }, wireItem],
    });

    const page = await fetchPointHealth(DEFAULT_HEALTH_QUERY);

    expect(page.rows.map((r) => r.pointId)).toEqual(["PT001"]);
  });

  it("total / limit / offset が欠けていれば要求内容と行数から補う", async () => {
    listGet.mockResolvedValue({ items: [wireItem] });

    const page = await fetchPointHealth({
      ...DEFAULT_HEALTH_QUERY,
      limit: 50,
      offset: 50,
    });

    expect(page.total).toBe(1);
    expect(page.limit).toBe(50);
    expect(page.offset).toBe(50);
  });

  it("dataComplete と indexState を正規化する（暫定判定を取りこぼさない）", async () => {
    listGet.mockResolvedValue({
      ...wirePage,
      dataComplete: false,
      indexState: "Warming",
    });

    const page = await fetchPointHealth(DEFAULT_HEALTH_QUERY);

    expect(page.dataComplete).toBe(false);
    expect(page.indexState).toBe("warming");
  });

  it("dataComplete / indexState が欠けていれば確定扱い（不在で警告を出さない）", async () => {
    listGet.mockResolvedValue({ items: [] });

    const page = await fetchPointHealth(DEFAULT_HEALTH_QUERY);

    expect(page.dataComplete).toBe(true);
    expect(page.indexState).toBe("ready");
    expect(page.rows).toEqual([]);
  });

  it("応答そのものが壊れていても空頁として返す", async () => {
    listGet.mockResolvedValue(null);

    const page = await fetchPointHealth(DEFAULT_HEALTH_QUERY);

    expect(page.rows).toEqual([]);
    expect(page.total).toBe(0);
  });

  it("失敗はステータス付きの日本語メッセージで投げ直す", async () => {
    listGet.mockRejectedValue({ response: { status: 503 } });

    await expect(fetchPointHealth(DEFAULT_HEALTH_QUERY)).rejects.toThrow(
      /データ品質.*503/,
    );
  });

  it("トークンを aspida クライアントに渡す（SSR 用）", async () => {
    listGet.mockResolvedValue(wirePage);

    await fetchPointHealth(DEFAULT_HEALTH_QUERY, "tok");

    expect(seenTokens).toEqual(["tok"]);
  });
});

describe("fetchPointHealthSummary", () => {
  const wireSummary = {
    totalPoints: 12482,
    fresh: 12210,
    stale: 183,
    missing: 47,
    unknown: 0,
    alarmWarn: 30,
    alarmCritical: 12,
    dataComplete: true,
    indexState: "Ready",
  };

  it("集計が受け付ける条件だけを送る（軸の絞り込みやページングは送らない）", async () => {
    summaryGet.mockResolvedValue(wireSummary);

    await fetchPointHealthSummary({
      ...DEFAULT_HEALTH_QUERY,
      buildingDtId: "dtmi:b1",
      floorDtId: "dtmi:f1",
      deviceDtId: "dtmi:ahu01",
      gatewayId: "GW-001",
      tags: ["hvac"],
      q: "給気",
      // 以下は集計では意味を持たない（軸別の内訳そのものを返す API なので）。
      freshness: ["stale"],
      alarm: ["warn"],
      healthStatus: ["critical"],
      olderThanSeconds: 600,
      limit: 50,
      offset: 100,
    });

    expect(summaryGet).toHaveBeenCalledWith({
      query: {
        buildingDtId: "dtmi:b1",
        floorDtId: "dtmi:f1",
        deviceDtId: "dtmi:ahu01",
        gatewayId: "GW-001",
        tag: ["hvac"],
        q: "給気",
      },
    });
  });

  it("軸別の内訳をそのまま写す", async () => {
    summaryGet.mockResolvedValue(wireSummary);

    expect(await fetchPointHealthSummary(DEFAULT_HEALTH_QUERY)).toEqual({
      totalPoints: 12482,
      fresh: 12210,
      stale: 183,
      missing: 47,
      unknown: 0,
      alarmWarn: 30,
      alarmCritical: 12,
      dataComplete: true,
      indexState: "ready",
    });
  });

  it("欠けた件数は 0、壊れた応答でも既定値で返す", async () => {
    summaryGet.mockResolvedValue({
      missing: 47,
      dataComplete: false,
      indexState: "Degraded",
    });

    expect(await fetchPointHealthSummary(DEFAULT_HEALTH_QUERY)).toEqual({
      totalPoints: 0,
      fresh: 0,
      stale: 0,
      missing: 47,
      unknown: 0,
      alarmWarn: 0,
      alarmCritical: 0,
      dataComplete: false,
      indexState: "degraded",
    });
  });

  it("失敗はステータス付きの日本語メッセージで投げ直す", async () => {
    summaryGet.mockRejectedValue({ response: { status: 500 } });

    await expect(fetchPointHealthSummary(DEFAULT_HEALTH_QUERY)).rejects.toThrow(
      /データ品質.*500/,
    );
  });
});
