import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { SystemStatus } from "@/lib/system-status/types";
import { SystemStatusView } from "./system-status-view";

const base: SystemStatus = {
  services: [
    { name: "api-server", status: "up" },
    { name: "nats", status: "down" },
  ],
  kpis: { msgRate1m: 1240, controlReq5m: 3 },
  metricsAvailable: true,
};

describe("SystemStatusView", () => {
  it("renders each service with its name and state label", () => {
    render(<SystemStatusView status={base} />);
    expect(screen.getByTestId("service-api-server")).toHaveTextContent("api-server");
    expect(screen.getByTestId("service-api-server")).toHaveTextContent("稼働");
    expect(screen.getByTestId("service-nats")).toHaveTextContent("停止");
  });

  it("renders KPI cards with formatted values", () => {
    render(<SystemStatusView status={base} />);
    expect(screen.getByTestId("kpi-msg-rate")).toHaveTextContent("1,240 msg/s");
    expect(screen.getByTestId("kpi-control-req")).toHaveTextContent("3 件");
  });

  it("degrades KPIs to an em dash and shows a banner when metrics are unavailable", () => {
    render(
      <SystemStatusView
        status={{
          services: [{ name: "api-server", status: "up" }],
          kpis: { msgRate1m: null, controlReq5m: null },
          metricsAvailable: false,
        }}
      />,
    );
    expect(screen.getByTestId("metrics-unavailable")).toBeInTheDocument();
    expect(screen.getByTestId("kpi-msg-rate")).toHaveTextContent("—");
    // services still render even without metrics
    expect(screen.getByTestId("service-api-server")).toHaveTextContent("稼働");
  });

  it("hides the metrics banner when metrics are available", () => {
    render(<SystemStatusView status={base} />);
    expect(screen.queryByTestId("metrics-unavailable")).toBeNull();
  });

  it("shows a Grafana deep link only when a URL is configured", () => {
    const { rerender } = render(<SystemStatusView status={base} />);
    expect(screen.queryByTestId("grafana-link")).toBeNull();

    rerender(<SystemStatusView status={base} grafanaUrl="https://grafana.example/d/abc" />);
    const link = screen.getByTestId("grafana-link");
    expect(link).toHaveAttribute("href", "https://grafana.example/d/abc");
  });
});
