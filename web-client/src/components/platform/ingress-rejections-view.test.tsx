import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { IngressRejectionStats } from "@/lib/ingress-rejections/types";
import { IngressRejectionsView } from "./ingress-rejections-view";

const base: IngressRejectionStats = {
  rejections: [
    { reason: "no_building_path", count: 30 },
    { reason: "unknown_point", count: 15 },
  ],
  metricsAvailable: true,
};

describe("IngressRejectionsView", () => {
  it("renders each rejection reason with its count", () => {
    render(<IngressRejectionsView stats={base} />);
    expect(screen.getByTestId("rejection-no_building_path")).toHaveTextContent("30");
    expect(screen.getByTestId("rejection-unknown_point")).toHaveTextContent("15");
  });

  it("shows a graceful empty state when metrics are unavailable", () => {
    render(<IngressRejectionsView stats={{ rejections: [], metricsAvailable: false }} />);
    expect(screen.getByTestId("metrics-unavailable")).toBeInTheDocument();
    expect(screen.queryByTestId(/^rejection-/)).toBeNull();
  });

  it("shows a no-rejections message when metrics are available but nothing was rejected", () => {
    render(<IngressRejectionsView stats={{ rejections: [], metricsAvailable: true }} />);
    expect(screen.getByTestId("no-rejections")).toBeInTheDocument();
    expect(screen.queryByTestId("metrics-unavailable")).toBeNull();
  });
});
