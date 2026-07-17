import { describe, expect, it } from "vitest";
import {
  autoGranularity,
  effectiveRange,
  resolveGranularity,
  spansMultipleDays,
  type PeriodPreset,
} from "./range";

const NOW = new Date("2026-07-17T12:00:00.000Z");

describe("effectiveRange", () => {
  it.each<[PeriodPreset, number]>([
    ["1h", 60 * 60 * 1000],
    ["24h", 24 * 60 * 60 * 1000],
    ["7d", 7 * 24 * 60 * 60 * 1000],
    ["30d", 30 * 24 * 60 * 60 * 1000],
  ])("ends at now and starts one %s span before", (preset, ms) => {
    const { start, end } = effectiveRange(preset, NOW);
    expect(end).toBe(NOW);
    expect(end.getTime() - start.getTime()).toBe(ms);
  });
});

describe("autoGranularity", () => {
  it("keeps sub-day spans raw and rolls longer spans up", () => {
    expect(autoGranularity("1h")).toBe("raw");
    expect(autoGranularity("24h")).toBe("raw");
    expect(autoGranularity("7d")).toBe("hour");
    expect(autoGranularity("30d")).toBe("day");
  });
});

describe("resolveGranularity", () => {
  it("derives from the preset when auto", () => {
    expect(resolveGranularity("auto", "7d")).toBe("hour");
    expect(resolveGranularity("auto", "24h")).toBe("raw");
  });

  it("passes an explicit choice through regardless of preset", () => {
    expect(resolveGranularity("day", "1h")).toBe("day");
    expect(resolveGranularity("raw", "30d")).toBe("raw");
  });
});

describe("spansMultipleDays", () => {
  it("is true only for multi-day presets", () => {
    expect(spansMultipleDays("1h")).toBe(false);
    expect(spansMultipleDays("24h")).toBe(false);
    expect(spansMultipleDays("7d")).toBe(true);
    expect(spansMultipleDays("30d")).toBe(true);
  });
});
