import type { TelemetryLatestSample } from "@/lib/telemetry/types";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { PointHealthPanel } from "./point-health-panel";

const NOW = new Date("2026-09-10T10:41:42Z");
const ago = (seconds: number) =>
  new Date(NOW.getTime() - seconds * 1000).toISOString();

const numeric = (v: number, secondsAgo: number): TelemetryLatestSample => ({
  t: ago(secondsAgo),
  value: { kind: "number", value: v },
});

function renderPanel(
  props: Partial<React.ComponentProps<typeof PointHealthPanel>> = {},
) {
  return render(
    <PointHealthPanel
      latest={numeric(23.4, 60)}
      now={NOW}
      staleThresholdSeconds={300}
      staleIntervalMultiplier={3}
      {...props}
    />,
  );
}

describe("PointHealthPanel (#457)", () => {
  it("explains why a point is stale: badge + interval × multiplier formula", () => {
    // 期待周期 5 分 × 3 = 15 分 に対して 18 分前の受信 → 鮮度切れ。
    renderPanel({
      latest: numeric(23.4, 18 * 60),
      expectedIntervalSeconds: 300,
    });

    expect(screen.getByTestId("point-health-panel")).toBeInTheDocument();
    expect(screen.getByTestId("freshness-stale")).toBeInTheDocument();
    expect(screen.getByTestId("health-expected-interval")).toHaveTextContent(
      "5分",
    );
    const threshold = screen.getByTestId("health-threshold");
    expect(threshold).toHaveTextContent("期待周期 5分 × 3 = 15分");
    expect(threshold).toHaveTextContent("超過");
  });

  it("shows the latest value with its unit and the last-seen time with a relative age", () => {
    renderPanel({ latest: numeric(23.4, 60), unit: "degC" });

    expect(screen.getByTestId("health-latest-value")).toHaveTextContent(
      "23.4 °C",
    );
    expect(screen.getByTestId("health-last-seen")).toHaveTextContent("1分前");
  });

  it("applies the point's scale to the latest value", () => {
    renderPanel({ latest: numeric(234, 60), scale: 0.1, unit: "degC" });

    expect(screen.getByTestId("health-latest-value")).toHaveTextContent(
      "23.4 °C",
    );
  });

  it("renders a non-numeric latest reading as text", () => {
    renderPanel({
      latest: { t: ago(30), value: { kind: "boolean", value: true } },
    });

    expect(screen.getByTestId("health-latest-value")).toHaveTextContent("ON");
  });

  it("falls back to the system default threshold when the point has no expected interval", () => {
    renderPanel({ latest: numeric(23.4, 60), expectedIntervalSeconds: null });

    expect(screen.getByTestId("freshness-fresh")).toBeInTheDocument();
    expect(screen.getByTestId("health-expected-interval")).toHaveTextContent(
      "未設定",
    );
    expect(screen.getByTestId("health-threshold")).toHaveTextContent(
      "既定閾値 5分",
    );
    // 未受信ではないので「超過」表記は出さない。
    expect(screen.getByTestId("health-threshold")).not.toHaveTextContent(
      "超過",
    );
  });

  it("reads as 欠測 when the point has never reported", () => {
    renderPanel({ latest: null });

    expect(screen.getByTestId("freshness-missing")).toBeInTheDocument();
    expect(screen.getByTestId("health-last-seen")).toHaveTextContent(
      "受信なし",
    );
    expect(screen.getByTestId("health-latest-value")).toHaveTextContent("-");
  });

  it("names the breached alarm threshold when the point has one configured", () => {
    renderPanel({
      latest: numeric(32, 60),
      unit: "degC",
      alarmThresholds: { alarmHigh: 30, warnHigh: 28 },
    });

    expect(screen.getByTestId("health-alarm")).toHaveTextContent(
      "異常上限 30 °C 超過（現在値 32 °C）",
    );
  });

  it("hides the alarm row when the point has no thresholds configured", () => {
    renderPanel({ latest: numeric(32, 60), alarmThresholds: {} });

    expect(screen.queryByTestId("health-alarm")).not.toBeInTheDocument();
  });

  it("shows the owning device when known and hides the row otherwise", () => {
    const { unmount } = renderPanel({ deviceName: "AHU-01" });
    expect(screen.getByTestId("health-device")).toHaveTextContent("AHU-01");
    unmount();

    renderPanel({ deviceName: null });
    expect(screen.queryByTestId("health-device")).not.toBeInTheDocument();
  });
});
