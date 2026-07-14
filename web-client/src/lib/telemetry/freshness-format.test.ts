import { describe, expect, it } from "vitest";
import { formatAge, freshnessLabel } from "./freshness-format";
import type { PointFreshness } from "./freshness";

describe("formatAge", () => {
  it("renders sub-minute ages in seconds", () => {
    expect(formatAge(0)).toBe("0秒前");
    expect(formatAge(45)).toBe("45秒前");
    expect(formatAge(59)).toBe("59秒前");
  });

  it("renders minutes (floored) below an hour", () => {
    expect(formatAge(60)).toBe("1分前");
    expect(formatAge(90)).toBe("1分前");
    expect(formatAge(3599)).toBe("59分前");
  });

  it("renders hours (floored) below a day", () => {
    expect(formatAge(3600)).toBe("1時間前");
    expect(formatAge(86399)).toBe("23時間前");
  });

  it("renders days at and beyond a day", () => {
    expect(formatAge(86400)).toBe("1日前");
    expect(formatAge(200000)).toBe("2日前");
  });
});

describe("freshnessLabel", () => {
  const fresh: PointFreshness = { pointId: "P", status: "fresh", ageSeconds: 10 };
  const stale: PointFreshness = { pointId: "P", status: "stale", ageSeconds: 900 };
  const missing: PointFreshness = { pointId: "P", status: "missing", ageSeconds: null };

  it("labels a fresh point simply as 最新", () => {
    expect(freshnessLabel(fresh)).toBe("最新");
  });

  it("labels a stale point with how long ago its last sample was", () => {
    expect(freshnessLabel(stale)).toBe("鮮度切れ（15分前）");
  });

  it("labels a missing point as 欠測", () => {
    expect(freshnessLabel(missing)).toBe("欠測");
  });

  it("falls back to plain 鮮度切れ when a stale point has no age", () => {
    expect(freshnessLabel({ pointId: "P", status: "stale", ageSeconds: null })).toBe(
      "鮮度切れ",
    );
  });
});
