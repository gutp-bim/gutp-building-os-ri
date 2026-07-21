import type { ValidTelemetryData } from "@/lib/infra/aspida-client/generated/@types";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { TelemetryHotData } from "./telemetry-hot-data";

function renderHot(
  hotData: ValidTelemetryData | null,
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

  it("uses the point's expected interval to bucket freshness (#183)", () => {
    // A fast point (expected 5s → threshold 15s) is stale at 60s old, even though the 300s default
    // would still call it fresh.
    renderHot({ datetime: iso(60), value: 21.5 }, 5);
    expect(screen.getByTestId("freshness-stale")).toBeInTheDocument();
  });

  it("keeps a slow point fresh past the 300s default when its interval is long (#183)", () => {
    // Expected daily (86400s → threshold 259200s): a 100000s-old sample is stale by the default but
    // fresh once the point's own interval is honored.
    renderHot({ datetime: iso(100_000), value: 21.5 }, 86_400);
    expect(screen.getByTestId("freshness-fresh")).toBeInTheDocument();
  });
});

describe("TelemetryHotData non-numeric value display (#152)", () => {
  // The aspida ValidTelemetryData type does not yet carry the discriminated value fields (regen is a
  // follow-up), so cast the literal — the runtime shape is what the API returns.
  const withValue = (v: Record<string, unknown>) =>
    ({ datetime: iso(10), ...v }) as unknown as ValidTelemetryData;

  it("shows a numeric value with unit unchanged", () => {
    renderHot({ datetime: iso(10), value: 21.5 });
    expect(screen.getByText(/21\.5/)).toBeInTheDocument();
  });

  it("shows a string reading as text", () => {
    renderHot(withValue({ valueType: "string", valueText: "auto" }));
    expect(screen.getByText("auto")).toBeInTheDocument();
  });

  it("shows a boolean reading as ON/OFF", () => {
    renderHot(withValue({ valueType: "boolean", valueBool: true }));
    expect(screen.getByText("ON")).toBeInTheDocument();
  });

  it("prefers a first-class string over the deprecated labels workaround (#152 Phase C)", () => {
    // A point that carries BOTH a first-class string value AND legacy `labels`: the string wins; the
    // numeric-code→label index mapping is not consulted.
    render(
      <TelemetryHotData
        hotData={withValue({ value: 2, valueType: "string", valueText: "運転" })}
        hotLoading={false}
        onRefresh={vi.fn()}
        onDownloadClick={vi.fn()}
        labels="停止,冷房,暖房"
      />,
    );
    expect(screen.getByText("運転")).toBeInTheDocument();
    expect(screen.queryByText("冷房")).not.toBeInTheDocument(); // labels[value-1] not used
  });

  it("still resolves a legacy numeric-code point via labels (back-compat)", () => {
    // A legacy enum point (numeric code + `labels`, no discriminant) keeps working.
    render(
      <TelemetryHotData
        hotData={{ datetime: iso(10), value: 2 }}
        hotLoading={false}
        onRefresh={vi.fn()}
        onDownloadClick={vi.fn()}
        labels="停止,冷房,暖房"
      />,
    );
    expect(screen.getByText("冷房")).toBeInTheDocument(); // labels[2-1]
  });
});
