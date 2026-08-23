import { describe, expect, it } from "vitest";
import { toTelemetryCsv } from "./csv";

const iso = "2026-01-01T00:00:00Z";

describe("toTelemetryCsv", () => {
  it("emits a header and one row per reading", () => {
    const csv = toTelemetryCsv({
      series: [{ t: iso, v: 21.5 }],
      state: [],
    });

    const lines = csv.split("\n");
    expect(lines[0]).toContain("日時,値");
    expect(lines).toHaveLength(2);
    expect(lines[1]).toContain("21.5");
  });

  // The download exists to hand over the telemetry the operator is looking at. A non-numeric point's
  // trend is the state timeline, and it was dropped entirely — the file came back as a bare header,
  // with no error, so it read as "there is no data".
  it("includes non-numeric readings, which the state timeline shows", () => {
    const csv = toTelemetryCsv({
      series: [],
      state: [{ t: iso, state: "occupied" }],
    });

    expect(csv.split("\n")).toHaveLength(2);
    expect(csv).toContain("occupied");
  });

  it("merges numeric and state readings in time order for a mixed point", () => {
    const csv = toTelemetryCsv({
      series: [
        { t: "2026-01-01T00:00:00Z", v: 1 },
        { t: "2026-01-01T02:00:00Z", v: 3 },
      ],
      state: [{ t: "2026-01-01T01:00:00Z", state: "auto" }],
    });

    const values = csv
      .split("\n")
      .slice(1)
      .map((l) => l.split(",").at(-1));
    expect(values).toEqual(["1", "auto", "3"]);
  });

  // Values were interpolated straight into the line. Harmless while only numbers could appear;
  // the moment a state string can, a comma or a quote corrupts every downstream column.
  it("escapes values containing a comma, a quote or a newline", () => {
    const csv = toTelemetryCsv({
      series: [],
      state: [
        { t: iso, state: 'alarm,"high"' },
        { t: "2026-01-01T00:01:00Z", state: "line1\nline2" },
      ],
    });

    expect(csv).toContain('"alarm,""high"""');
    expect(csv).toContain('"line1\nline2"');
  });

  // Excel reads a UTF-8 file without a BOM as the system codepage, so the Japanese header came out
  // as mojibake — for a file whose whole audience opens it in a spreadsheet.
  it("starts with a UTF-8 BOM so Excel reads the Japanese header correctly", () => {
    expect(toTelemetryCsv({ series: [], state: [] }).charCodeAt(0)).toBe(
      0xfeff,
    );
  });

  it("emits only the header when the point has no readings in range", () => {
    expect(toTelemetryCsv({ series: [], state: [] }).split("\n")).toHaveLength(
      1,
    );
  });
});
