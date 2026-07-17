import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi, type Mock } from "vitest";

// Stub the heavy children so the test isolates the page's own telemetry-error wiring (#196).
vi.mock("next/navigation", () => ({ useRouter: () => ({ back: vi.fn() }) }));
vi.mock("./components/telemetry-hot-data", () => ({ TelemetryHotData: () => <div data-testid="hot" /> }));
vi.mock("./components/telemetry-warm-data", () => ({ TelemetryWarmData: () => <div data-testid="warm" /> }));
vi.mock("./components/point-info", () => ({ PointInfo: () => <div /> }));
vi.mock("./components/point-control-modal/point-control-modal", () => ({
  PointControlModal: () => <div />,
}));
vi.mock("./components/control-audit-history", () => ({ ControlAuditHistory: () => <div /> }));
vi.mock("./components/cold-data-download-modal", () => ({ ColdDataDownloadModal: () => <div /> }));
vi.mock("@/lib/resources/repository", () => ({ getPointDetail: vi.fn() }));
vi.mock("@/lib/telemetry/repository", () => ({
  latestTelemetry: vi.fn(),
  queryTelemetry: vi.fn(),
}));

import { getPointDetail } from "@/lib/resources/repository";
import { latestTelemetry, queryTelemetry } from "@/lib/telemetry/repository";
import PointDetailPageComponent from "./page-component";

const detail = {
  point: { id: "p1", name: "室温" },
  device: {},
  floor: {},
  space: {},
};

afterEach(() => vi.clearAllMocks());

describe("PointDetailPageComponent telemetry-error surfacing (#196)", () => {
  it("shows inline banners when the hot and warm reads fail instead of failing silently", async () => {
    (getPointDetail as Mock).mockResolvedValue(detail);
    (latestTelemetry as Mock).mockRejectedValue(new Error("hot down"));
    (queryTelemetry as Mock).mockRejectedValue(new Error("warm down"));
    vi.spyOn(console, "error").mockImplementation(() => {});

    render(<PointDetailPageComponent pointId="p1" />);

    expect(await screen.findByTestId("hot-error")).toHaveTextContent("最新値の取得に失敗しました");
    expect(await screen.findByTestId("warm-error")).toHaveTextContent("履歴データの取得に失敗しました");
  });

  it("shows no error banners when the reads succeed", async () => {
    (getPointDetail as Mock).mockResolvedValue(detail);
    (latestTelemetry as Mock).mockResolvedValue({ t: "2026-07-17T00:00:00Z", v: 1 });
    (queryTelemetry as Mock).mockResolvedValue({ pointId: "p1", points: [] });

    render(<PointDetailPageComponent pointId="p1" />);

    // Wait for the async load to settle (the hot stub renders once the point detail resolves).
    expect(await screen.findByTestId("hot")).toBeInTheDocument();
    expect(screen.queryByTestId("hot-error")).not.toBeInTheDocument();
    expect(screen.queryByTestId("warm-error")).not.toBeInTheDocument();
  });
});
