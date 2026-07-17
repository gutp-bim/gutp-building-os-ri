import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { TelemetryWarmData } from "./telemetry-warm-data";

function setup(overrides: Partial<Parameters<typeof TelemetryWarmData>[0]> = {}) {
  const props = {
    warmData: [],
    warmLoading: false,
    onRefresh: vi.fn(),
    period: "24h" as const,
    granularity: "auto" as const,
    onPeriodChange: vi.fn(),
    onGranularityChange: vi.fn(),
    ...overrides,
  };
  render(<TelemetryWarmData {...props} />);
  return props;
}

describe("TelemetryWarmData (#197)", () => {
  it("reflects the selected period and granularity in the controls", () => {
    setup({ period: "7d", granularity: "hour" });
    expect(screen.getByTestId("warm-period-select")).toHaveValue("7d");
    expect(screen.getByTestId("warm-granularity-select")).toHaveValue("hour");
  });

  it("raises onPeriodChange when the period is changed", () => {
    const { onPeriodChange } = setup();
    fireEvent.change(screen.getByTestId("warm-period-select"), { target: { value: "30d" } });
    expect(onPeriodChange).toHaveBeenCalledWith("30d");
  });

  it("raises onGranularityChange when the granularity is changed", () => {
    const { onGranularityChange } = setup();
    fireEvent.change(screen.getByTestId("warm-granularity-select"), { target: { value: "day" } });
    expect(onGranularityChange).toHaveBeenCalledWith("day");
  });

  it("shows the unit in the header when provided", () => {
    setup({ unit: "°C" });
    expect(screen.getByText(/単位: °C/)).toBeInTheDocument();
  });

  it("shows an empty state when there is no data and it is not loading", () => {
    setup({ warmData: [], warmLoading: false });
    expect(screen.getByText("データがありません")).toBeInTheDocument();
  });
});
