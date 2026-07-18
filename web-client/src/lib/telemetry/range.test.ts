import { describe, expect, it } from "vitest";
import {
  autoGranularity,
  autoGranularityForSpan,
  dateRangeError,
  effectiveRange,
  isValidDateRange,
  rangeSpansMultipleDays,
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

const iso = (s: string) => new Date(s);

describe("autoGranularityForSpan", () => {
  it("mirrors the preset cutoffs for a custom span", () => {
    expect(
      autoGranularityForSpan(
        iso("2026-07-17T00:00:00Z"),
        iso("2026-07-17T06:00:00Z"),
      ),
    ).toBe("raw"); // 6h ≤ 1 day
    expect(
      autoGranularityForSpan(
        iso("2026-07-17T00:00:00Z"),
        iso("2026-07-18T00:00:00Z"),
      ),
    ).toBe("raw"); // exactly 1 day
    expect(
      autoGranularityForSpan(
        iso("2026-07-10T00:00:00Z"),
        iso("2026-07-17T00:00:00Z"),
      ),
    ).toBe("hour"); // 7 days
    expect(
      autoGranularityForSpan(
        iso("2026-06-17T00:00:00Z"),
        iso("2026-07-17T00:00:00Z"),
      ),
    ).toBe("day"); // 30 days
  });
});

describe("rangeSpansMultipleDays", () => {
  it("is true only when the range exceeds one day", () => {
    expect(
      rangeSpansMultipleDays(
        iso("2026-07-17T00:00:00Z"),
        iso("2026-07-17T18:00:00Z"),
      ),
    ).toBe(false);
    expect(
      rangeSpansMultipleDays(
        iso("2026-07-17T00:00:00Z"),
        iso("2026-07-19T00:00:00Z"),
      ),
    ).toBe(true);
  });
});

describe("dateRangeError", () => {
  it("returns null while the pair is incomplete (no nagging)", () => {
    expect(dateRangeError("", "", NOW)).toBeNull();
    expect(dateRangeError("2026-07-17T00:00", "", NOW)).toBeNull();
    expect(dateRangeError("", "2026-07-17T00:00", NOW)).toBeNull();
  });

  it("rejects start ≥ end", () => {
    expect(dateRangeError("2026-07-17T10:00", "2026-07-17T09:00", NOW)).toBe(
      "開始日時は終了日時より前にしてください。",
    );
    expect(dateRangeError("2026-07-17T09:00", "2026-07-17T09:00", NOW)).toBe(
      "開始日時は終了日時より前にしてください。",
    );
  });

  it("rejects a future end and a future start", () => {
    expect(dateRangeError("2026-07-17T09:00", "2026-07-18T09:00", NOW)).toBe(
      "終了日時に未来の日時は指定できません。",
    );
    expect(dateRangeError("2026-07-18T09:00", "2026-07-19T09:00", NOW)).toBe(
      "終了日時に未来の日時は指定できません。",
    );
  });

  it("accepts a complete, past, ordered pair", () => {
    expect(
      dateRangeError("2026-07-17T09:00", "2026-07-17T11:00", NOW),
    ).toBeNull();
  });
});

describe("isValidDateRange", () => {
  it("is true only for a complete, valid pair", () => {
    expect(isValidDateRange("2026-07-17T09:00", "2026-07-17T11:00", NOW)).toBe(
      true,
    );
    expect(isValidDateRange("", "2026-07-17T11:00", NOW)).toBe(false);
    expect(isValidDateRange("2026-07-17T11:00", "2026-07-17T09:00", NOW)).toBe(
      false,
    );
    expect(isValidDateRange("2026-07-17T09:00", "2026-07-18T11:00", NOW)).toBe(
      false,
    );
  });
});
