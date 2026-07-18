import type { GatewayAdminView } from "@/lib/admin/gateways";
import type { HomeLoaders } from "@/lib/home/loaders";
import type { ResourceRef } from "@/lib/resources/types";
import type { PointFreshness } from "@/lib/telemetry/freshness";
import { render, screen, waitFor, within } from "@testing-library/react";
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
      loadFreshness: vi
        .fn()
        .mockResolvedValue(
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
