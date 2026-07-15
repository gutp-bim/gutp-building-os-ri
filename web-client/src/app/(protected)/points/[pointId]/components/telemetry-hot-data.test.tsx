import type { ValidTelemetryData } from "@/lib/infra/aspida-client/generated/@types";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { TelemetryHotData } from "./telemetry-hot-data";

function renderHot(hotData: ValidTelemetryData | null) {
  return render(
    <TelemetryHotData
      hotData={hotData}
      hotLoading={false}
      onRefresh={vi.fn()}
      onDownloadClick={vi.fn()}
      unit="degC"
    />,
  );
}

// Timestamps relative to real `now` (the component computes freshness with `new Date()`); the offsets
// are far from the 300s default threshold so the tiny setup→render delay can't flip the bucket.
const iso = (secondsAgo: number) =>
  new Date(Date.now() - secondsAgo * 1000).toISOString();

describe("TelemetryHotData freshness badge (#158)", () => {
  it("shows a fresh badge for a just-received sample", () => {
    renderHot({ datetime: iso(10), value: 21.5 });
    expect(screen.getByTestId("freshness-fresh")).toBeInTheDocument();
  });

  it("shows a stale badge for an old sample", () => {
    renderHot({ datetime: iso(100_000), value: 21.5 });
    expect(screen.getByTestId("freshness-stale")).toBeInTheDocument();
  });

  it("shows no freshness badge when there is no hot data", () => {
    renderHot(null);
    expect(screen.queryByTestId(/^freshness-/)).not.toBeInTheDocument();
  });
});
