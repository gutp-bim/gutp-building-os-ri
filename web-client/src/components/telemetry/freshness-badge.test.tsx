import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { FreshnessBadge } from "./freshness-badge";
import type { PointFreshness } from "@/lib/telemetry/freshness";

const fresh: PointFreshness = { pointId: "P", status: "fresh", ageSeconds: 10 };
const stale: PointFreshness = { pointId: "P", status: "stale", ageSeconds: 900 };
const missing: PointFreshness = { pointId: "P", status: "missing", ageSeconds: null };

describe("FreshnessBadge", () => {
  it("renders a fresh point as 最新 with the fresh testid", () => {
    render(<FreshnessBadge freshness={fresh} />);
    expect(screen.getByTestId("freshness-fresh")).toHaveTextContent("最新");
  });

  it("renders a stale point with its age", () => {
    render(<FreshnessBadge freshness={stale} />);
    expect(screen.getByTestId("freshness-stale")).toHaveTextContent(
      "鮮度切れ（15分前）",
    );
  });

  it("renders a missing point as 欠測", () => {
    render(<FreshnessBadge freshness={missing} />);
    expect(screen.getByTestId("freshness-missing")).toHaveTextContent("欠測");
  });

  it("distinguishes the three statuses with different classes", () => {
    const { rerender } = render(<FreshnessBadge freshness={fresh} />);
    const freshClass = screen.getByTestId("freshness-fresh").className;
    rerender(<FreshnessBadge freshness={stale} />);
    const staleClass = screen.getByTestId("freshness-stale").className;
    rerender(<FreshnessBadge freshness={missing} />);
    const missingClass = screen.getByTestId("freshness-missing").className;
    expect(new Set([freshClass, staleClass, missingClass]).size).toBe(3);
  });
});
