import { describe, expect, it } from "vitest";
import type { PointAlarm } from "./alarm";
import {
  explainAlarm,
  explainStaleThreshold,
  formatDurationJa,
} from "./threshold-explain";

describe("formatDurationJa", () => {
  it("keeps sub-minute spans in seconds", () => {
    expect(formatDurationJa(0)).toBe("0秒");
    expect(formatDurationJa(5)).toBe("5秒");
    expect(formatDurationJa(45)).toBe("45秒");
  });

  it("rounds up to the largest natural unit", () => {
    expect(formatDurationJa(60)).toBe("1分");
    expect(formatDurationJa(300)).toBe("5分");
    expect(formatDurationJa(3600)).toBe("1時間");
    expect(formatDurationJa(7200)).toBe("2時間");
    expect(formatDurationJa(86400)).toBe("1日");
    expect(formatDurationJa(172800)).toBe("2日");
  });

  it("shows one decimal for a span that does not divide evenly", () => {
    expect(formatDurationJa(90)).toBe("1.5分");
    expect(formatDurationJa(5400)).toBe("1.5時間");
    expect(formatDurationJa(100000)).toBe("1.2日");
  });

  it("treats a non-finite or negative span as 0秒", () => {
    expect(formatDurationJa(-10)).toBe("0秒");
    expect(formatDurationJa(Number.NaN)).toBe("0秒");
    expect(formatDurationJa(Number.POSITIVE_INFINITY)).toBe("0秒");
  });
});

describe("explainStaleThreshold", () => {
  it("explains the interval × multiplier formula when the point has an expected interval", () => {
    const r = explainStaleThreshold({
      expectedIntervalSeconds: 300,
      staleThresholdSeconds: 300,
      staleIntervalMultiplier: 3,
    });
    expect(r.thresholdSeconds).toBe(900);
    expect(r.usesExpectedInterval).toBe(true);
    expect(r.text).toBe("期待周期 5分 × 3 = 15分");
  });

  it("explains the system default when the point has no expected interval", () => {
    const r = explainStaleThreshold({
      expectedIntervalSeconds: null,
      staleThresholdSeconds: 300,
      staleIntervalMultiplier: 3,
    });
    expect(r.thresholdSeconds).toBe(300);
    expect(r.usesExpectedInterval).toBe(false);
    expect(r.text).toBe(
      "既定閾値 5分（期待周期が未設定のため倍率は適用されません）",
    );
  });

  it("falls back to the default multiplier when none / an unusable one is supplied", () => {
    expect(
      explainStaleThreshold({
        expectedIntervalSeconds: 60,
        staleThresholdSeconds: 300,
      }).text,
    ).toBe("期待周期 1分 × 3 = 3分");
    expect(
      explainStaleThreshold({
        expectedIntervalSeconds: 60,
        staleThresholdSeconds: 300,
        staleIntervalMultiplier: 0,
      }).thresholdSeconds,
    ).toBe(180);
  });

  it("falls back to the registry default threshold when none is supplied", () => {
    const r = explainStaleThreshold({ expectedIntervalSeconds: null });
    expect(r.thresholdSeconds).toBe(300);
    expect(r.text).toContain("既定閾値 5分");
  });

  it("renders a non-integer multiplier verbatim in the formula", () => {
    expect(
      explainStaleThreshold({
        expectedIntervalSeconds: 60,
        staleThresholdSeconds: 300,
        staleIntervalMultiplier: 2.5,
      }).text,
    ).toBe("期待周期 1分 × 2.5 = 2.5分");
  });
});

const alarm = (a: Partial<PointAlarm>): PointAlarm => ({
  pointId: "p1",
  status: "ok",
  value: 22,
  breach: null,
  ...a,
});

describe("explainAlarm", () => {
  it("names the breached critical high threshold with the current value", () => {
    expect(
      explainAlarm(
        alarm({ status: "critical", value: 32, breach: "high" }),
        { alarmHigh: 30, warnHigh: 28 },
        "°C",
      ),
    ).toBe("異常上限 30 °C 超過（現在値 32 °C）");
  });

  it("names the breached warn low threshold", () => {
    expect(
      explainAlarm(
        alarm({ status: "warn", value: 8, breach: "low" }),
        { alarmLow: 5, warnLow: 10 },
        "°C",
      ),
    ).toBe("注意下限 10 °C 下回り（現在値 8 °C）");
  });

  it("omits the unit when the point has none", () => {
    expect(
      explainAlarm(alarm({ status: "critical", value: 32, breach: "high" }), {
        alarmHigh: 30,
      }),
    ).toBe("異常上限 30 超過（現在値 32）");
  });

  it("reports 正常範囲内 when thresholds are configured and not breached", () => {
    expect(
      explainAlarm(alarm({ status: "ok", value: 22 }), { alarmHigh: 30 }),
    ).toBe("正常範囲内");
  });

  it("returns null when the point has no thresholds / no value to judge", () => {
    expect(
      explainAlarm(alarm({ status: "unknown", value: null }), {}),
    ).toBeNull();
  });
});
