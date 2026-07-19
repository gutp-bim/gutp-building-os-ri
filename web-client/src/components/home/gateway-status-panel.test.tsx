import type { GatewayAdminView } from "@/lib/admin/gateways";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { GatewayStatusPanel } from "./gateway-status-panel";

const gateway: GatewayAdminView = {
  gatewayId: "GW-SOS-001",
  bindingType: "bacnet-sim",
  settings: {},
  pointCount: 8,
  revision: "sha256:abcdef1234567890",
  certTrustAnchor: "",
  lastTelemetryAt: null,
  connected: false,
  pointlistSynced: null,
};

describe("GatewayStatusPanel", () => {
  it("renders each gateway with binding label, point count and short revision", async () => {
    render(
      <GatewayStatusPanel
        fetchGateways={vi.fn().mockResolvedValue([gateway])}
      />,
    );
    const row = await screen.findByTestId("home-gateway-row");
    expect(row).toHaveTextContent("GW-SOS-001");
    expect(row).toHaveTextContent("BACnet Sim");
    expect(row).toHaveTextContent("8 ポイント");
    expect(row).toHaveTextContent("abcdef123456"); // first 12 hex chars of the sha256 revision
  });

  it("labels itself as registration info, not connection state (#181)", async () => {
    render(
      <GatewayStatusPanel
        fetchGateways={vi.fn().mockResolvedValue([gateway])}
      />,
    );
    expect(await screen.findByText("登録済みゲートウェイ")).toBeInTheDocument();
    expect(screen.getByTestId("home-gateway-panel-note")).toHaveTextContent(
      "接続状態",
    );
  });

  it("shows the live egress connection state (#230)", async () => {
    render(
      <GatewayStatusPanel
        fetchGateways={vi
          .fn()
          .mockResolvedValue([{ ...gateway, connected: true }])}
      />,
    );
    expect(
      await screen.findByTestId("home-gateway-connected"),
    ).toHaveTextContent("接続中");
  });

  it("shows 未接続 when there is no live egress stream (#230)", async () => {
    render(
      <GatewayStatusPanel
        fetchGateways={vi.fn().mockResolvedValue([gateway])}
      />,
    );
    expect(
      await screen.findByTestId("home-gateway-connected"),
    ).toHaveTextContent("未接続");
  });

  it("shows the tri-state pointlist sync badge (#230 Phase 2b)", async () => {
    const { rerender } = render(
      <GatewayStatusPanel
        fetchGateways={vi
          .fn()
          .mockResolvedValue([{ ...gateway, pointlistSynced: true }])}
      />,
    );
    expect(
      await screen.findByTestId("home-gateway-pointlist-synced"),
    ).toHaveTextContent("同期済み");

    rerender(
      <GatewayStatusPanel
        fetchGateways={vi
          .fn()
          .mockResolvedValue([{ ...gateway, pointlistSynced: false }])}
      />,
    );
    expect(
      await screen.findByTestId("home-gateway-pointlist-synced"),
    ).toHaveTextContent("未同期");
  });

  it("shows a derived last-seen (受信なし when the gateway has not reported, #181)", async () => {
    render(
      <GatewayStatusPanel
        fetchGateways={vi.fn().mockResolvedValue([gateway])}
      />,
    );
    expect(
      await screen.findByTestId("home-gateway-last-seen"),
    ).toHaveTextContent("最終受信: 受信なし");
  });

  it("shows the empty state when no gateways are registered", async () => {
    render(
      <GatewayStatusPanel fetchGateways={vi.fn().mockResolvedValue([])} />,
    );
    expect(
      await screen.findByText("ゲートウェイは登録されていません。"),
    ).toBeInTheDocument();
  });

  it("shows an error message when the fetch fails", async () => {
    render(
      <GatewayStatusPanel
        fetchGateways={vi.fn().mockRejectedValue(new Error("403 forbidden"))}
      />,
    );
    expect(await screen.findByText("403 forbidden")).toBeInTheDocument();
  });
});
