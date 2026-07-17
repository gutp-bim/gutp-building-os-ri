import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi, type Mock } from "vitest";

// Stub the heavy children so the test isolates the page's own telemetry wiring (#196 / #197).
vi.mock("next/navigation", () => ({ useRouter: () => ({ back: vi.fn() }) }));
vi.mock("./components/telemetry-hot-data", () => ({ TelemetryHotData: () => <div data-testid="hot" /> }));
vi.mock("./components/telemetry-warm-data", () => ({
  // Expose the received data + a way to change the period so the out-of-order test can drive it.
  TelemetryWarmData: ({
    warmData,
    onPeriodChange,
  }: {
    warmData: { value?: number }[];
    onPeriodChange: (p: string) => void;
  }) => (
    <div>
      <div data-testid="warm-values">{warmData.map((d) => d.value).join(",")}</div>
      <button data-testid="pick-30d" onClick={() => onPeriodChange("30d")}>
        30d
      </button>
      <button data-testid="pick-1h" onClick={() => onPeriodChange("1h")}>
        1h
      </button>
    </div>
  ),
}));
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

const series = (v: number) => ({ pointId: "p1", points: [{ t: "2026-07-17T00:00:00Z", v }] });

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

    expect(await screen.findByTestId("hot")).toBeInTheDocument();
    expect(screen.queryByTestId("hot-error")).not.toBeInTheDocument();
    expect(screen.queryByTestId("warm-error")).not.toBeInTheDocument();
  });
});

describe("PointDetailPageComponent out-of-order warm responses (#197 review)", () => {
  it("ignores a stale warm response so a slow superseded request can't overwrite the chart", async () => {
    (getPointDetail as Mock).mockResolvedValue(detail);
    (latestTelemetry as Mock).mockResolvedValue({ t: "2026-07-17T00:00:00Z", v: 1 });

    // Each queryTelemetry call captures its own resolver so we can settle them out of order.
    const resolvers: ((v: unknown) => void)[] = [];
    (queryTelemetry as Mock).mockImplementation(
      () => new Promise((resolve) => resolvers.push(resolve)),
    );

    const user = userEvent.setup();
    render(<PointDetailPageComponent pointId="p1" />);

    // Initial warm request (default 24h); settle it so the chart starts populated.
    await waitFor(() => expect(resolvers).toHaveLength(1));
    resolvers[0](series(24));
    await screen.findByText("24");

    // Select 30d (request A, slow), then 1h (request B, the latest).
    await user.click(screen.getByTestId("pick-30d"));
    await waitFor(() => expect(resolvers).toHaveLength(2));
    await user.click(screen.getByTestId("pick-1h"));
    await waitFor(() => expect(resolvers).toHaveLength(3));

    // B (latest) resolves first with 1, then A (stale) resolves later with 30.
    resolvers[2](series(1));
    await screen.findByText("1");
    resolvers[1](series(30));

    // The stale A response must not overwrite B's chart.
    await waitFor(() => expect(screen.getByTestId("warm-values")).toHaveTextContent("1"));
    expect(screen.getByTestId("warm-values")).not.toHaveTextContent("30");
  });
});
