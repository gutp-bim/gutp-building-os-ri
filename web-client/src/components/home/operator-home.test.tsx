import type { GatewayAdminView } from "@/lib/admin/gateways";
import type { HealthSummary } from "@/lib/health/repository";
import type { HomeLoaders } from "@/lib/home/loaders";
import type { ResourceRef } from "@/lib/resources/types";
import type { PointAlarm } from "@/lib/telemetry/alarm";
import type { PointFreshness } from "@/lib/telemetry/freshness";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { OperatorHome } from "./operator-home";

const building: ResourceRef = {
  type: "building",
  dtId: "b1",
  id: "b1",
  name: "棟A",
};
const floor: ResourceRef = { type: "floor", dtId: "f1", id: "f1", name: "1F" };

const namedPoints = [
  { pointId: "p1", name: "室温", deviceName: "AHU-1", spaceName: "会議室A" },
  { pointId: "p2", name: "湿度", deviceName: "AHU-1", spaceName: "会議室A" },
  {
    pointId: "p3",
    name: "CO2",
    deviceName: "CO2-Sensor-01",
    spaceName: "会議室A",
  },
];

const freshness: PointFreshness[] = [
  { pointId: "p1", status: "fresh", ageSeconds: 10 },
  { pointId: "p2", status: "stale", ageSeconds: 900 },
  { pointId: "p3", status: "missing", ageSeconds: null },
];

const healthSummary: HealthSummary = {
  totalPoints: 3,
  fresh: 1,
  stale: 1,
  missing: 1,
  unknown: 0,
  alarmWarn: 0,
  alarmCritical: 0,
  dataComplete: true,
  indexState: "ready",
};

function makeLoaders(overrides: Partial<HomeLoaders> = {}): HomeLoaders {
  return {
    loadBuildings: vi.fn().mockResolvedValue([building]),
    loadFloors: vi.fn().mockResolvedValue([floor]),
    loadFloorPoints: vi.fn().mockResolvedValue(namedPoints),
    loadFreshness: vi.fn().mockResolvedValue(freshness),
    loadAlarms: vi.fn().mockResolvedValue([]),
    loadOperationsSummary: vi.fn().mockResolvedValue({
      msgRate1m: null,
      msgRate1hAvg: null,
      metricsAvailable: false,
    }),
    loadHealthSummary: vi.fn().mockResolvedValue(healthSummary),
    ...overrides,
  };
}

const gateway: GatewayAdminView = {
  gatewayId: "GW-1",
  bindingType: "bacnet-sim",
  settings: {},
  pointCount: 8,
  revision: "sha256:abcdef1234567890",
  certTrustAnchor: "",
  lastTelemetryAt: null,
  connected: false,
  pointlistSynced: null,
};

describe("OperatorHome", () => {
  it("shows fresh/stale/missing counts once a floor auto-loads", async () => {
    const loadHealthSummary = vi.fn().mockResolvedValue(healthSummary);
    render(
      <OperatorHome
        loaders={makeLoaders({ loadHealthSummary })}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    await waitFor(() => {
      expect(
        within(screen.getByTestId("summary-fresh")).getByText("1"),
      ).toBeInTheDocument();
      expect(
        within(screen.getByTestId("summary-stale")).getByText("1"),
      ).toBeInTheDocument();
      expect(
        within(screen.getByTestId("summary-missing")).getByText("1"),
      ).toBeInTheDocument();
    });
    // Scoped to the selected building + floor, not re-derived from loadFreshness (#451).
    expect(loadHealthSummary).toHaveBeenCalledWith("b1", "f1");
  });

  it("lists only attention points, missing first then stale", async () => {
    render(
      <OperatorHome
        loaders={makeLoaders()}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    const rows = await screen.findAllByTestId("home-attention-row");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent("CO2"); // missing sorts first
    expect(rows[0]).toHaveTextContent("欠測");
    expect(rows[1]).toHaveTextContent("湿度"); // stale
  });

  it("surfaces value alarms above freshness, with a count and value label (#158 Phase 2a)", async () => {
    const alarms: PointAlarm[] = [
      { pointId: "p1", status: "critical", value: 40, breach: "high" }, // p1 was fresh
    ];
    render(
      <OperatorHome
        loaders={makeLoaders({ loadAlarms: vi.fn().mockResolvedValue(alarms) })}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    await waitFor(() =>
      expect(
        within(screen.getByTestId("summary-alarm")).getByText("1"),
      ).toBeInTheDocument(),
    );
    const rows = await screen.findAllByTestId("home-attention-row");
    // critical alarm sorts before missing/stale.
    expect(rows[0]).toHaveTextContent("室温");
    expect(rows[0]).toHaveTextContent("異常値（上限 40）");
    expect(rows).toHaveLength(3); // p1 critical + p2 stale + p3 missing
  });

  it("links each attention row to the point detail and shows its space/device", async () => {
    render(
      <OperatorHome
        loaders={makeLoaders()}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    const links = await screen.findAllByTestId("home-attention-link");
    // p3 (CO2) is missing → sorts first.
    expect(links[0]).toHaveAttribute("href", "/points/p3");
    expect(links[0]).toHaveTextContent("CO2");
    expect(links[0]).toHaveTextContent("会議室A");
    expect(links[0]).toHaveTextContent("CO2-Sensor-01");
  });

  it("shows the empty state when every point is fresh", async () => {
    const loaders = makeLoaders({
      loadFreshness: vi.fn().mockResolvedValue(
        namedPoints.map((p) => ({
          pointId: p.pointId,
          status: "fresh",
          ageSeconds: 1,
        })),
      ),
    });
    render(
      <OperatorHome
        loaders={loaders}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );
    expect(
      await screen.findByTestId("home-attention-empty"),
    ).toBeInTheDocument();
  });

  it("aggregates every floor's attention points when すべてのフロア is selected (#158 Phase 2)", async () => {
    const floor2: ResourceRef = {
      type: "floor",
      dtId: "f2",
      id: "f2",
      name: "2F",
    };
    // Each floor contributes one stale point; the building-wide view must show both.
    const loadFloorPoints = vi.fn().mockImplementation((floorDtId: string) =>
      Promise.resolve(
        floorDtId === "f1"
          ? [
              {
                pointId: "p1",
                name: "1F室温",
                deviceName: "AHU-1",
                spaceName: "会議室A",
              },
            ]
          : [
              {
                pointId: "p2",
                name: "2F室温",
                deviceName: "AHU-2",
                spaceName: "会議室B",
              },
            ],
      ),
    );
    const loadFreshness = vi
      .fn()
      .mockImplementation((points: { pointId: string }[]) =>
        Promise.resolve(
          points.map((p) => ({
            pointId: p.pointId,
            status: "stale" as const,
            ageSeconds: 900,
          })),
        ),
      );
    const loadHealthSummary = vi.fn().mockResolvedValue(healthSummary);
    const loaders = makeLoaders({
      loadFloors: vi.fn().mockResolvedValue([floor, floor2]),
      loadFloorPoints,
      loadFreshness,
      loadHealthSummary,
    });

    render(
      <OperatorHome
        loaders={loaders}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    // The first floor auto-loads (one row); switching to すべてのフロア aggregates both.
    await screen.findAllByTestId("home-attention-row");
    expect(loadHealthSummary).toHaveBeenCalledWith("b1", "f1");
    await userEvent.selectOptions(
      screen.getByTestId("home-floor-select"),
      "__all__",
    );

    await waitFor(() => {
      expect(screen.getAllByTestId("home-attention-row")).toHaveLength(2);
    });
    expect(loadFloorPoints).toHaveBeenCalledWith("f1");
    expect(loadFloorPoints).toHaveBeenCalledWith("f2");
    expect(screen.getByText("1F室温")).toBeInTheDocument();
    expect(screen.getByText("2F室温")).toBeInTheDocument();
    // "すべてのフロア" scopes the server-side summary to the whole building (no floorDtId),
    // rather than fanning out a per-floor client aggregation (#451).
    expect(loadHealthSummary).toHaveBeenCalledWith("b1", undefined);
  });

  it("hides the gateway panel for non-admins and shows it for admins", async () => {
    const fetchGateways = vi.fn().mockResolvedValue([gateway]);

    const { rerender } = render(
      <OperatorHome
        loaders={makeLoaders()}
        isAdmin={false}
        fetchGateways={fetchGateways}
      />,
    );
    await screen.findAllByTestId("home-attention-row");
    expect(screen.queryByTestId("home-gateway-panel")).not.toBeInTheDocument();

    rerender(
      <OperatorHome
        loaders={makeLoaders()}
        isAdmin={true}
        fetchGateways={fetchGateways}
      />,
    );
    expect(await screen.findByTestId("home-gateway-panel")).toBeInTheDocument();
    expect(await screen.findByTestId("home-gateway-row")).toHaveTextContent(
      "GW-1",
    );
  });

  it("surfaces an error when the building load fails", async () => {
    const loaders = makeLoaders({
      loadBuildings: vi.fn().mockRejectedValue(new Error("boom")),
    });
    render(
      <OperatorHome
        loaders={loaders}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );
    expect(await screen.findByTestId("home-error")).toHaveTextContent("boom");
  });

  it("shows the registered-point total and the fresh rate from the server summary (#451)", async () => {
    // 1,234 points: 1,200 fresh + 34 stale → 97.2%. The thousands separator and the 1-decimal
    // percentage are part of the contract. The total/fresh rate come from `GET
    // /api/telemetry/health/summary` (#452) directly, not from re-aggregating `loadFreshness`.
    const loaders = makeLoaders({
      loadHealthSummary: vi.fn().mockResolvedValue({
        totalPoints: 1234,
        fresh: 1200,
        stale: 34,
        missing: 0,
        unknown: 0,
        alarmWarn: 0,
        alarmCritical: 0,
        dataComplete: true,
        indexState: "ready",
      } satisfies HealthSummary),
    });
    render(
      <OperatorHome
        loaders={loaders}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    const total = await screen.findByTestId("summary-total");
    await waitFor(() => expect(total).toHaveTextContent("1,234"));
    expect(total).toHaveTextContent("登録ポイント");
    const fresh = screen.getByTestId("summary-fresh");
    expect(fresh).toHaveTextContent("1,200");
    expect(fresh).toHaveTextContent("97.2%");
    // 到着軸のラベルは「最新」（/health の freshnessLabel と同じ語）。値の正常性ではないので
    // 「正常」とは呼ばない。
    expect(fresh).toHaveTextContent("最新");
    expect(fresh).not.toHaveTextContent("正常");
  });

  it("shows a dash instead of a fresh rate when no point is registered (#451 Phase 1)", async () => {
    const loaders = makeLoaders({
      loadFloorPoints: vi.fn().mockResolvedValue([]),
      loadFreshness: vi.fn().mockResolvedValue([]),
      loadHealthSummary: vi.fn().mockResolvedValue({
        totalPoints: 0,
        fresh: 0,
        stale: 0,
        missing: 0,
        unknown: 0,
        alarmWarn: 0,
        alarmCritical: 0,
        dataComplete: true,
        indexState: "ready",
      } satisfies HealthSummary),
    });
    render(
      <OperatorHome
        loaders={loaders}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    await waitFor(() =>
      expect(screen.getByTestId("summary-total")).toHaveTextContent("0"),
    );
    expect(screen.getByTestId("summary-fresh")).toHaveTextContent("—");
    expect(screen.getByTestId("summary-fresh")).not.toHaveTextContent("%");
  });

  it("surfaces an error when the health summary fetch fails, instead of showing zero counts (#451)", async () => {
    const loaders = makeLoaders({
      loadHealthSummary: vi
        .fn()
        .mockRejectedValue(new Error("データ品質の集計取得に失敗しました (503)")),
    });
    render(
      <OperatorHome
        loaders={loaders}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );
    expect(await screen.findByTestId("home-error")).toHaveTextContent(
      "データ品質の集計取得に失敗しました",
    );
  });

  it("links each summary card to the matching /health filter (#451 Phase 1)", async () => {
    render(
      <OperatorHome
        loaders={makeLoaders()}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    await screen.findAllByTestId("home-attention-row");
    const href = (testid: string) =>
      screen.getByTestId(testid).getAttribute("href");
    expect(href("summary-total")).toBe("/health");
    expect(href("summary-fresh")).toBe("/health?freshness=fresh");
    expect(href("summary-stale")).toBe("/health?freshness=stale");
    expect(href("summary-missing")).toBe("/health?freshness=missing");
    expect(href("summary-alarm")).toBe("/health?alarm=warn,critical");
  });

  it("links to the data-quality screen from the attention list, even when it is empty (#451 Phase 1)", async () => {
    render(
      <OperatorHome
        loaders={makeLoaders()}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );
    const link = await screen.findByTestId("home-attention-all-link");
    expect(link).toHaveAttribute("href", "/health");
    expect(link).toHaveTextContent("データ品質");

    // 要対応が 0 件でも導線は出す。
    const allFresh = makeLoaders({
      loadFreshness: vi.fn().mockResolvedValue(
        namedPoints.map((p) => ({
          pointId: p.pointId,
          status: "fresh",
          ageSeconds: 1,
        })),
      ),
    });
    render(
      <OperatorHome
        loaders={allFresh}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );
    await screen.findByTestId("home-attention-empty");
    expect(screen.getAllByTestId("home-attention-all-link")).toHaveLength(2);
  });

  it("surfaces a telemetry error instead of showing every point as missing (#182 review)", async () => {
    // When the freshness batch fetch fails, the view must not silently classify all points as
    // missing — it shows the error banner so the operator knows the data is unavailable, not absent.
    const loaders = makeLoaders({
      loadFreshness: vi
        .fn()
        .mockRejectedValue(new Error("最新値の一括取得に失敗しました (503)")),
    });
    render(
      <OperatorHome
        loaders={loaders}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );
    expect(await screen.findByTestId("home-error")).toHaveTextContent(
      "最新値の一括取得に失敗しました",
    );
    expect(screen.queryByTestId("home-attention-row")).not.toBeInTheDocument();
  });

  it("shows the data-throughput card with the 1h-average delta when Prometheus is wired (#451 Phase 1)", async () => {
    const loaders = makeLoaders({
      loadOperationsSummary: vi.fn().mockResolvedValue({
        msgRate1m: 1842,
        msgRate1hAvg: 1790,
        metricsAvailable: true,
      }),
    });
    render(
      <OperatorHome
        loaders={loaders}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    expect(await screen.findByTestId("throughput-current")).toHaveTextContent(
      "1,842 msg/s",
    );
    expect(screen.getByTestId("throughput-1h-avg")).toHaveTextContent(
      "1,790 msg/s",
    );
    expect(screen.getByTestId("throughput-delta")).toHaveTextContent("↑ 2.9%");
  });

  it("hides the data-throughput card when Prometheus is not configured (#451 Phase 1)", async () => {
    render(
      <OperatorHome
        loaders={makeLoaders()}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    await screen.findAllByTestId("home-attention-row");
    expect(screen.queryByTestId("home-throughput")).not.toBeInTheDocument();
  });

  it("hides the data-throughput card when Prometheus is configured but unreachable (#451 Phase 1)", async () => {
    // The default docker-compose stack sets PROMETHEUS_URL even without the observability profile
    // (CLAUDE.md) so Prometheus is unreachable, not "unconfigured" — metricsAvailable comes back
    // true but both scalars are null. The card must still stay hidden, not show em dashes.
    const loaders = makeLoaders({
      loadOperationsSummary: vi.fn().mockResolvedValue({
        msgRate1m: null,
        msgRate1hAvg: null,
        metricsAvailable: true,
      }),
    });
    render(
      <OperatorHome
        loaders={loaders}
        isAdmin={false}
        fetchGateways={vi.fn()}
      />,
    );

    await screen.findAllByTestId("home-attention-row");
    expect(screen.queryByTestId("home-throughput")).not.toBeInTheDocument();
  });
});
