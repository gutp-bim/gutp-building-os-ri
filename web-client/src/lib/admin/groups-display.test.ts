import { describe, expect, it } from "vitest";
import { formatDate } from "./groups-display";

describe("formatDate", () => {
  it("formats an ISO timestamp as a ja-JP date", () => {
    // 2024-03-09T12:00:00Z → ja-JP date string contains the year/month/day
    expect(formatDate("2024-03-09T12:00:00Z")).toBe(
      new Date("2024-03-09T12:00:00Z").toLocaleDateString("ja-JP"),
    );
  });

  it("returns a dash for missing values", () => {
    expect(formatDate(undefined)).toBe("—");
    expect(formatDate(null)).toBe("—");
    expect(formatDate("")).toBe("—");
  });

  it("returns a dash for an unparseable value", () => {
    expect(formatDate("not-a-date")).toBe("—");
  });
});
