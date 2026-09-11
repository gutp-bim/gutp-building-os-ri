import type { GatewayAdminView } from "@/lib/admin/gateways";
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

function makeLoaders(overrides: Partial<HomeLoaders> = {}): HomeLoaders {
  return {
    loadBuildings: vi.fn().mockResolvedValue([building]),
    loadFloors: vi.fn().mockResolvedValue([floor]),
    loadFloorPoints: vi.fn().mockResolvedValue(namedPoints),
    loadFreshness: vi.fn().mockResolvedValue(freshness),
    loadAlarms: vi.fn().mockResolvedValue([]),
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
    render(
      <OperatorHome
        loaders={makeLoaders()}
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
    const loaders = makeLoaders({
      loadFloors: vi.fn().mockResolvedValue([floor, floor2]),
      loadFloorPoints,
      loadFreshness,
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

  it("shows the registered-point total and the fresh rate (#451 Phase 1)", async () => {
    // 1,234 points: 1,200 fresh + 34 stale → 97.2%. The thousands separator and the 1-decimal
    // percentage are part of the contract.
    const points = Array.from({ length: 1234 }, (_, i) => ({
      pointId: `p${i}`,
      name: `点${i}`,
      deviceName: "AHU-1",
      spaceName: "会議室A",
    }));
    const loaders = makeLoaders({
      loadFloorPoints: vi.fn().mockResolvedValue(points),
      loadFreshness: vi.fn().mockResolvedValue(
        points.map((p, i) => ({
          pointId: p.pointId,
          status: i < 1200 ? ("fresh" as const) : ("stale" as const),
          ageSeconds: i < 1200 ? 1 : 900,
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
});
