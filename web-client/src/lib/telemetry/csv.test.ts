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

    // The timestamp field can itself be quoted and contain a comma under a non-ja locale (a
    // small-ICU Node falls back to en-US: `1/1/2026, 9:00:00 AM`), so take the value as everything
    // after the last unquoted comma rather than naively splitting.
    const values = csv
      .split("\n")
      .slice(1)
      .map((l) => l.replace(/^(?:"(?:[^"]|"")*"|[^,]*),/, ""));
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

  // Excel evaluates a cell whose text starts with = + - @ as a formula. A state string is operator
  // -supplied data reaching a spreadsheet, so it has to be neutralised — RFC 4180 quoting does not
  // do it (Excel strips the quotes, then evaluates).
  it("neutralises values Excel would evaluate as a formula", () => {
    const csv = toTelemetryCsv({
      series: [],
      state: [
        { t: iso, state: "=1+1" },
        { t: "2026-01-01T00:01:00Z", state: "+44" },
        { t: "2026-01-01T00:02:00Z", state: "-cmd" },
        { t: "2026-01-01T00:03:00Z", state: "@SUM(A1)" },
      ],
    });

    for (const dangerous of ["=1+1", "+44", "-cmd", "@SUM(A1)"]) {
      expect(csv).not.toContain(`,${dangerous}`);
    }
    expect(csv).toContain("'=1+1");
  });

  // A negative reading is not a formula — prefixing it would corrupt the number.
  it("leaves a negative numeric reading alone", () => {
    const csv = toTelemetryCsv({ series: [{ t: iso, v: -3.5 }], state: [] });

    expect(csv).toContain(",-3.5");
    expect(csv).not.toContain("'-3.5");
  });

  // An aggregate bucket carries a numeric average AND a state representative, so the same timestamp
  // can appear in both inputs. A two-column CSV cannot express both; emitting two rows with the same
  // timestamp and contradictory values is worse than picking one. Numeric wins, matching
  // `resolveTelemetryValue`'s numeric-first precedence.
  //
  // The download forces raw, where the two series are true complements, so this is a guard on the
  // pure function rather than a shape the screen produces today.
  it("emits one row per timestamp when both a numeric and a state reading share it", () => {
    const csv = toTelemetryCsv({
      series: [{ t: iso, v: 42 }],
      state: [{ t: iso, state: "auto" }],
    });

    const lines = csv.split("\n");
    expect(lines).toHaveLength(2);
    expect(lines[1]).toContain("42");
    expect(lines[1]).not.toContain("auto");
  });
});
