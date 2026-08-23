import type { TelemetryLatestSample } from "@/lib/telemetry/types";
import type { ResolvedTelemetryValue } from "@/lib/telemetry/value";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { TelemetryHotData } from "./telemetry-hot-data";

function renderHot(
  hotData: TelemetryLatestSample | null,
  expectedIntervalSeconds?: number | null,
) {
  return render(
    <TelemetryHotData
      hotData={hotData}
      hotLoading={false}
      onRefresh={vi.fn()}
      onDownloadClick={vi.fn()}
      unit="degC"
      expectedIntervalSeconds={expectedIntervalSeconds}
    />,
  );
}

// Timestamps relative to real `now` (the component computes freshness with `new Date()`); the offsets
// are far from the 300s default threshold so the tiny setup→render delay can't flip the bucket.
const iso = (secondsAgo: number) =>
  new Date(Date.now() - secondsAgo * 1000).toISOString();

const sample = (
  value: ResolvedTelemetryValue,
  secondsAgo = 10,
): TelemetryLatestSample => ({ t: iso(secondsAgo), value });

const numeric = (v: number, secondsAgo = 10) =>
  sample({ kind: "number", value: v }, secondsAgo);

describe("TelemetryHotData freshness badge (#158)", () => {
  it("shows a fresh badge for a just-received sample", () => {
    renderHot(numeric(21.5));
    expect(screen.getByTestId("freshness-fresh")).toBeInTheDocument();
  });

  it("shows a stale badge for an old sample", () => {
    renderHot(numeric(21.5, 100_000));
    expect(screen.getByTestId("freshness-stale")).toBeInTheDocument();
  });

  it("shows no freshness badge when there is no hot data", () => {
    renderHot(null);
    expect(screen.queryByTestId(/^freshness-/)).not.toBeInTheDocument();
  });

  it("uses the point's expected interval to bucket freshness (#183)", () => {
    // A fast point (expected 5s → threshold 15s) is stale at 60s old, even though the 300s default
    // would still call it fresh.
    renderHot(numeric(21.5, 60), 5);
    expect(screen.getByTestId("freshness-stale")).toBeInTheDocument();
  });

  it("keeps a slow point fresh past the 300s default when its interval is long (#183)", () => {
    // Expected daily (86400s → threshold 259200s): a 100000s-old sample is stale by the default but
    // fresh once the point's own interval is honored.
    renderHot(numeric(21.5, 100_000), 86_400);
    expect(screen.getByTestId("freshness-fresh")).toBeInTheDocument();
  });
});

describe("TelemetryHotData non-numeric value display (#152)", () => {
  it("shows a numeric value with unit unchanged", () => {
    renderHot(numeric(21.5));
    expect(screen.getByText(/21\.5/)).toBeInTheDocument();
  });

  it("shows a string reading as text", () => {
    renderHot(sample({ kind: "string", value: "auto" }));
    expect(screen.getByText("auto")).toBeInTheDocument();
  });

  it("shows a boolean reading as ON/OFF", () => {
    renderHot(sample({ kind: "boolean", value: true }));
    expect(screen.getByText("ON")).toBeInTheDocument();
  });

  it("shows a dash when the resolved value is none", () => {
    renderHot(sample({ kind: "none" }));
    expect(screen.getByText("-")).toBeInTheDocument();
  });

  it("applies scale and unit only to the numeric reading", () => {
    // A string reading must not gain the unit suffix the numeric path appends.
    renderHot(sample({ kind: "string", value: "auto" }));
    expect(screen.getByText("auto")).toBeInTheDocument();
    expect(screen.queryByText(/auto\s*℃/)).not.toBeInTheDocument();
  });
});
