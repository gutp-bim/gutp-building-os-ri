import type { TelemetryStatePoint } from "@/lib/telemetry/types";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { TelemetryStateTimeline } from "./telemetry-state-timeline";

describe("TelemetryStateTimeline (#152 Phase B)", () => {
  const points: TelemetryStatePoint[] = [
    { t: "2026-01-01T01:00:00Z", state: "auto" },
    { t: "2026-01-01T02:00:00Z", state: "OFF" },
  ];

  it("renders readings newest-first", () => {
    render(<TelemetryStateTimeline points={points} loading={false} />);
    const rows = screen.getAllByTestId("state-timeline-row");
    expect(rows).toHaveLength(2);
    // Newest (OFF) is the first row.
    expect(rows[0]).toHaveTextContent("OFF");
    expect(rows[1]).toHaveTextContent("auto");
  });

  it("shows an empty message when there are no readings", () => {
    render(<TelemetryStateTimeline points={[]} loading={false} />);
    expect(screen.getByTestId("state-timeline-empty")).toBeInTheDocument();
  });

  it("shows a loading state", () => {
    render(<TelemetryStateTimeline points={[]} loading={true} />);
    expect(screen.getByText("読み込み中…")).toBeInTheDocument();
    expect(screen.queryByTestId("state-timeline-empty")).not.toBeInTheDocument();
  });

  it("does not mutate the caller's ascending array", () => {
    const input: TelemetryStatePoint[] = [...points];
    render(<TelemetryStateTimeline points={input} loading={false} />);
    expect(input[0].state).toBe("auto"); // still ascending
  });
});
