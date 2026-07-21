import type { ValidTelemetryData } from "@/lib/infra/aspida-client/generated/@types";
import { describe, expect, it } from "vitest";
import { toGranularityParam, toSeries, toStateSeries } from "./mapping";

// The aspida ValidTelemetryData type does not yet declare the discriminated value fields (#152) —
// cast literals that carry them; the runtime shape is what the API returns.
const row = (o: Record<string, unknown>) => o as unknown as ValidTelemetryData;

describe("toSeries", () => {
  it("sorts samples by datetime ascending", () => {
    const raw: ValidTelemetryData[] = [
      { datetime: "2026-01-01T03:00:00Z", value: 3 },
      { datetime: "2026-01-01T01:00:00Z", value: 1 },
      { datetime: "2026-01-01T02:00:00Z", value: 2 },
    ];
    const series = toSeries("PT001", raw);
    expect(series.pointId).toBe("PT001");
    expect(series.points.map((p) => p.v)).toEqual([1, 2, 3]);
  });

  it("drops samples missing a datetime or value", () => {
    const raw: ValidTelemetryData[] = [
      { datetime: "2026-01-01T01:00:00Z", value: 1 },
      { datetime: null, value: 9 },
      { datetime: "2026-01-01T02:00:00Z", value: null },
      { value: 5 },
    ];
    const series = toSeries("PT001", raw);
    expect(series.points).toEqual([{ t: "2026-01-01T01:00:00Z", v: 1 }]);
  });

  it("treats a zero value as present, not missing", () => {
    const raw: ValidTelemetryData[] = [
      { datetime: "2026-01-01T01:00:00Z", value: 0 },
    ];
    expect(toSeries("PT001", raw).points).toEqual([
      { t: "2026-01-01T01:00:00Z", v: 0 },
    ]);
  });

  it("returns an empty series for empty input", () => {
    expect(toSeries("PT001", []).points).toEqual([]);
  });
});

describe("toStateSeries", () => {
  it("keeps only non-numeric rows, ascending, formatted (#152 Phase B)", () => {
    const raw = [
      row({ datetime: "2026-01-01T03:00:00Z", valueType: "boolean", valueBool: true }),
      row({ datetime: "2026-01-01T01:00:00Z", valueType: "string", valueText: "auto" }),
      row({ datetime: "2026-01-01T02:00:00Z", value: 42, valueType: "number" }), // dropped (numeric)
      row({ datetime: null, valueType: "string", valueText: "x" }), // dropped (no datetime)
    ];
    const s = toStateSeries("PT001", raw);
    expect(s.pointId).toBe("PT001");
    expect(s.points).toEqual([
      { t: "2026-01-01T01:00:00Z", state: "auto" },
      { t: "2026-01-01T03:00:00Z", state: "ON" },
    ]);
  });

  it("returns an empty series for a purely numeric point", () => {
    const raw = [row({ datetime: "2026-01-01T01:00:00Z", value: 1, valueType: "number" })];
    expect(toStateSeries("PT001", raw).points).toEqual([]);
  });
});

describe("toGranularityParam", () => {
  it("maps each granularity to its backend enum ordinal", () => {
    expect(toGranularityParam("raw")).toBe(0);
    expect(toGranularityParam("hour")).toBe(1);
    expect(toGranularityParam("day")).toBe(2);
  });

  it("returns undefined when granularity is unset", () => {
    expect(toGranularityParam(undefined)).toBeUndefined();
  });
});
