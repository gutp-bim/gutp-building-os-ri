import { describe, expect, it } from "vitest";
import {
  classifyPointAlarm,
  classifyPointAlarms,
  summarizeAlarms,
} from "./alarm";

describe("classifyPointAlarm (#158 Phase 2a)", () => {
  it("is unknown when the point has no thresholds configured (opt-in only)", () => {
    expect(classifyPointAlarm({ pointId: "p", value: 999 }).status).toBe(
      "unknown",
    );
    expect(
      classifyPointAlarm({ pointId: "p", value: 999, thresholds: {} }).status,
    ).toBe("unknown");
    expect(
      classifyPointAlarm({
        pointId: "p",
        value: 999,
        thresholds: { alarmHigh: null, warnHigh: undefined },
      }).status,
    ).toBe("unknown");
  });

  it("is unknown when there is no usable current value", () => {
    const thresholds = { alarmHigh: 30 };
    expect(
      classifyPointAlarm({ pointId: "p", value: null, thresholds }).status,
    ).toBe("unknown");
    expect(
      classifyPointAlarm({ pointId: "p", value: Number.NaN, thresholds }).status,
    ).toBe("unknown");
  });

  it("flags a high-limit breach as critical with breach=high", () => {
    const r = classifyPointAlarm({
      pointId: "p",
      value: 31,
      thresholds: { alarmHigh: 30 },
    });
    expect(r.status).toBe("critical");
    expect(r.breach).toBe("high");
  });

  it("treats the threshold boundary as breached (>=, <=)", () => {
    expect(
      classifyPointAlarm({ pointId: "p", value: 30, thresholds: { alarmHigh: 30 } })
        .status,
    ).toBe("critical");
    expect(
      classifyPointAlarm({ pointId: "p", value: 5, thresholds: { alarmLow: 5 } })
        .status,
    ).toBe("critical");
  });

  it("flags a low-limit breach as critical with breach=low", () => {
    const r = classifyPointAlarm({
      pointId: "p",
      value: 2,
      thresholds: { alarmLow: 5 },
    });
    expect(r.status).toBe("critical");
    expect(r.breach).toBe("low");
  });

  it("is ok when the value is inside the band", () => {
    const r = classifyPointAlarm({
      pointId: "p",
      value: 22,
      thresholds: { alarmHigh: 30, alarmLow: 10 },
    });
    expect(r.status).toBe("ok");
    expect(r.breach).toBeNull();
  });

  it("uses warn bounds for the inner stage and critical for the outer", () => {
    const thresholds = { alarmHigh: 30, warnHigh: 26 };
    expect(
      classifyPointAlarm({ pointId: "p", value: 24, thresholds }).status,
    ).toBe("ok");
    expect(
      classifyPointAlarm({ pointId: "p", value: 27, thresholds }).status,
    ).toBe("warn");
    // Past the critical bound → critical wins even though it also passed warn.
    expect(
      classifyPointAlarm({ pointId: "p", value: 31, thresholds }).status,
    ).toBe("critical");
  });

  it("summarizes only actionable states (ok/unknown excluded)", () => {
    const results = classifyPointAlarms([
      { pointId: "a", value: 31, thresholds: { alarmHigh: 30 } }, // critical
      { pointId: "b", value: 27, thresholds: { alarmHigh: 30, warnHigh: 26 } }, // warn
      { pointId: "c", value: 20, thresholds: { alarmHigh: 30 } }, // ok
      { pointId: "d", value: 20 }, // unknown (no thresholds)
    ]);
    expect(summarizeAlarms(results)).toEqual({ critical: 1, warn: 1 });
  });
});
