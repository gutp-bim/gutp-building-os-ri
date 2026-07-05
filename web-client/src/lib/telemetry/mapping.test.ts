import type { ValidTelemetryData } from "@/lib/infra/aspida-client/generated/@types";
import { describe, expect, it } from "vitest";
import { toGranularityParam, toSeries } from "./mapping";

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
