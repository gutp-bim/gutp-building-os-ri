import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { GatewayAdminView } from "@/lib/admin/gateways";
import { GatewayStatusPanel } from "./gateway-status-panel";

const gateway: GatewayAdminView = {
  gatewayId: "GW-SOS-001",
  bindingType: "bacnet-sim",
  settings: {},
  pointCount: 8,
  revision: "sha256:abcdef1234567890",
  certTrustAnchor: "",
};

describe("GatewayStatusPanel", () => {
  it("renders each gateway with binding label, point count and short revision", async () => {
    render(<GatewayStatusPanel fetchGateways={vi.fn().mockResolvedValue([gateway])} />);
    const row = await screen.findByTestId("home-gateway-row");
    expect(row).toHaveTextContent("GW-SOS-001");
    expect(row).toHaveTextContent("BACnet Sim");
    expect(row).toHaveTextContent("8 ポイント");
    expect(row).toHaveTextContent("abcdef123456"); // first 12 hex chars of the sha256 revision
  });

  it("labels itself as registration info, not connection state (#181)", async () => {
    render(<GatewayStatusPanel fetchGateways={vi.fn().mockResolvedValue([gateway])} />);
    expect(await screen.findByText("登録済みゲートウェイ")).toBeInTheDocument();
    expect(screen.getByTestId("home-gateway-panel-note")).toHaveTextContent("接続状態");
  });

  it("shows the empty state when no gateways are registered", async () => {
    render(<GatewayStatusPanel fetchGateways={vi.fn().mockResolvedValue([])} />);
    expect(await screen.findByText("ゲートウェイは登録されていません。")).toBeInTheDocument();
  });

  it("shows an error message when the fetch fails", async () => {
    render(
      <GatewayStatusPanel fetchGateways={vi.fn().mockRejectedValue(new Error("403 forbidden"))} />,
    );
    expect(await screen.findByText("403 forbidden")).toBeInTheDocument();
  });
});
