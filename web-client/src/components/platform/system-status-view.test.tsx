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
  it("renders the pipeline KPIs with threshold levels and breakdown tooltips (#456)", () => {
    render(
      <SystemStatusView
        status={{
          ...base,
          kpis: {
            ...base.kpis,
            ingressRate1m: 1842,
            ingressBySource: [{ label: "gateway-grpc", value: 1842 }],
            validatedRate1m: 1240,
            rejectedRate1m: 40,
            rejectedPercent: 2.2,
            rejectedByResult: [{ label: "bad_payload", value: 40 }],
            eventLagP95Seconds: 1.2,
            consumerLagP95Seconds: 0.082,
            parquetFreshnessP95Seconds: 28,
            parquetDropped15m: 0,
            natsPending: 124,
          },
          thresholds: {
            rejectedPercentWarn: 1,
            eventLagP95WarnSeconds: 30,
            consumerLagP95WarnSeconds: 5,
            parquetFreshnessWarnSeconds: 600,
            natsPendingWarn: 1000,
          },
        }}
      />,
    );
    expect(screen.getByTestId("kpi-ingress")).toHaveTextContent("1,842 msg/s");
    expect(screen.getByTestId("kpi-rejected")).toHaveTextContent("40 msg/s (2.2 %)");
    expect(screen.getByTestId("kpi-rejected")).toHaveAttribute("data-level", "warn");
    expect(screen.getByTestId("kpi-rejected").getAttribute("title")).toContain("bad_payload: 40 msg/s");
    expect(screen.getByTestId("kpi-event-lag")).toHaveTextContent("1.2 s");
    expect(screen.getByTestId("kpi-event-lag")).toHaveAttribute("data-level", "ok");
    expect(screen.getByTestId("kpi-consumer-lag")).toHaveTextContent("82 ms");
    expect(screen.getByTestId("kpi-parquet-freshness")).toHaveTextContent("28 s");
    expect(screen.getByTestId("kpi-nats-pending")).toHaveTextContent("124");
    // The two lags carry their reading guide as a glossary tooltip.
    expect(screen.getByTestId("glossary-Event lag")).toBeInTheDocument();
    expect(screen.getByTestId("glossary-Consumer lag")).toBeInTheDocument();
  });

  it("shows the observability-profile empty state without Prometheus", () => {
    render(
      <SystemStatusView
        status={{ services: [], kpis: { msgRate1m: null, controlReq5m: null }, metricsAvailable: false }}
      />,
    );
    expect(screen.getByTestId("metrics-unavailable")).toHaveTextContent("observability");
    expect(screen.getByTestId("kpi-event-lag")).toHaveTextContent("—");
    expect(screen.getByTestId("kpi-event-lag")).toHaveAttribute("data-level", "nodata");
  });

  it("adds per-KPI Grafana links only when a URL is configured", () => {
    const { rerender } = render(<SystemStatusView status={base} />);
    expect(screen.queryByTestId("kpi-msg-rate-grafana")).toBeNull();

    rerender(<SystemStatusView status={base} grafanaUrl="https://grafana.example" />);
    expect(screen.getByTestId("kpi-msg-rate-grafana")).toHaveAttribute(
      "href",
      "https://grafana.example/d/building-os-overview/building-os-overview?viewPanel=5",
    );
    expect(screen.getByTestId("kpi-event-lag-grafana")).toHaveAttribute(
      "href",
      "https://grafana.example/d/building-os-overview/building-os-overview",
    );
  });

  it("gives each Grafana link a distinct accessible name (WCAG 2.4.4)", () => {
    render(<SystemStatusView status={base} grafanaUrl="https://grafana.example" />);
    const names = screen
      .getAllByRole("link", { name: /Grafana/ })
      .map((a) => a.getAttribute("aria-label") ?? a.textContent);
    expect(names.length).toBeGreaterThan(1);
    expect(new Set(names).size).toBe(names.length);
  });
});
