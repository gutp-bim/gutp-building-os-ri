import {
  bucketCoverage,
  COVERAGE_BUCKET_COUNT,
} from "@/lib/telemetry/coverage";
import { render, screen } from "@testing-library/react";
import axe from "axe-core";
import { describe, expect, it } from "vitest";
import { TelemetryCoverageBar } from "./telemetry-coverage-bar";

const END = new Date("2026-09-10T12:00:00Z");
const MIN = 60 * 1000;

/** 1 分周期で 24h 受信し、`skip` の区間（窓の終端からの分）だけ欠測させる。 */
function buckets(skip: (m: number) => boolean = () => false) {
  const timestamps: string[] = [];
  for (let m = 0.5; m < 24 * 60; m += 1) {
    if (!skip(m))
      timestamps.push(new Date(END.getTime() - m * MIN).toISOString());
  }
  return bucketCoverage({ timestamps, windowEnd: END, intervalSeconds: 60 });
}

describe("TelemetryCoverageBar (#457)", () => {
  it("draws one segment per bucket, tagged with its level", () => {
    render(
      <TelemetryCoverageBar
        state={{ kind: "buckets", buckets: buckets((m) => m >= 30 && m < 45) }}
      />,
    );

    const segments = screen.getAllByTestId(/^coverage-bucket-/);
    expect(segments).toHaveLength(COVERAGE_BUCKET_COUNT);
    expect(segments.at(-3)).toHaveAttribute("data-level", "none");
    expect(segments.at(-1)).toHaveAttribute("data-level", "full");
  });

  it("summarises the gaps as text so the bar is readable without colour", () => {
    render(
      <TelemetryCoverageBar
        state={{ kind: "buckets", buckets: buckets((m) => m >= 30 && m < 60) }}
      />,
    );

    const bar = screen.getByRole("img");
    expect(bar).toHaveAccessibleName(/欠測 2 区間/);
    expect(screen.getByTestId("coverage-summary")).toHaveTextContent(
      "最長欠測 30分",
    );
  });

  it("says there is no gap when every bucket is covered", () => {
    render(
      <TelemetryCoverageBar state={{ kind: "buckets", buckets: buckets() }} />,
    );

    expect(screen.getByTestId("coverage-summary")).toHaveTextContent(
      "欠測なし",
    );
  });

  it("explains why the bar is not drawn without an expected interval", () => {
    render(
      <TelemetryCoverageBar
        state={{ kind: "unavailable", reason: "no-interval" }}
      />,
    );

    expect(screen.getByTestId("coverage-unavailable")).toHaveTextContent(
      "期待更新周期",
    );
  });

  it("shows loading and error states", () => {
    const { rerender } = render(
      <TelemetryCoverageBar state={{ kind: "loading" }} />,
    );
    expect(screen.getByTestId("coverage-loading")).toBeInTheDocument();

    rerender(<TelemetryCoverageBar state={{ kind: "error" }} />);
    expect(screen.getByTestId("coverage-error")).toBeInTheDocument();
  });

  it("has no axe violations", async () => {
    const { container } = render(
      <TelemetryCoverageBar
        state={{ kind: "buckets", buckets: buckets((m) => m >= 30 && m < 45) }}
      />,
    );

    // jsdom はレイアウト・配色を計算しないので color-contrast だけ外す。
    const results = await axe.run(container, {
      rules: { "color-contrast": { enabled: false } },
    });
    expect(results.violations).toEqual([]);
  });
});
