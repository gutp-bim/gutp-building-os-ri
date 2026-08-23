import type {
  LatestSample,
  TelemetryReading,
} from "@/lib/infra/aspida-client/generated/@types";
import { describe, expect, it } from "vitest";
import {
  toGranularityParam,
  toLatestSample,
  toPointsLastSeen,
  toSeries,
  toStateSeries,
} from "./mapping";

describe("toSeries", () => {
  it("sorts samples by datetime ascending", () => {
    const raw: TelemetryReading[] = [
      { datetime: "2026-01-01T03:00:00Z", value: 3 },
      { datetime: "2026-01-01T01:00:00Z", value: 1 },
      { datetime: "2026-01-01T02:00:00Z", value: 2 },
    ];
    const series = toSeries("PT001", raw);
    expect(series.pointId).toBe("PT001");
    expect(series.points.map((p) => p.v)).toEqual([1, 2, 3]);
  });

  it("drops samples missing a datetime or value", () => {
    const raw: TelemetryReading[] = [
      { datetime: "2026-01-01T01:00:00Z", value: 1 },
      { datetime: null, value: 9 },
      { datetime: "2026-01-01T02:00:00Z", value: null },
      { value: 5 },
    ];
    const series = toSeries("PT001", raw);
    expect(series.points).toEqual([{ t: "2026-01-01T01:00:00Z", v: 1 }]);
  });

  it("treats a zero value as present, not missing", () => {
    const raw: TelemetryReading[] = [
      { datetime: "2026-01-01T01:00:00Z", value: 0 },
    ];
    expect(toSeries("PT001", raw).points).toEqual([
      { t: "2026-01-01T01:00:00Z", v: 0 },
    ]);
  });

  it("returns an empty series for empty input", () => {
    expect(toSeries("PT001", []).points).toEqual([]);
  });

  it("keeps a legacy row with no valueType as numeric", () => {
    const raw: TelemetryReading[] = [
      { datetime: "2026-01-01T01:00:00Z", value: 7 },
    ];
    expect(toSeries("PT001", raw).points).toEqual([
      { t: "2026-01-01T01:00:00Z", v: 7 },
    ]);
  });

  it("drops a non-numeric union reading from the numeric series", () => {
    const raw: TelemetryReading[] = [
      { datetime: "2026-01-01T01:00:00Z", value: "auto", valueType: "string" },
    ];
    expect(toSeries("PT001", raw).points).toEqual([]);
  });
});

describe("toSeries / toStateSeries", () => {
  it("assigns each row of a mixed point to exactly one of the two series", () => {
    const raw: TelemetryReading[] = [
      { datetime: "2026-01-01T01:00:00Z", value: 1, valueType: "number" },
      // #344/#359: the reading rides in `value`, and a raw non-numeric row repeats it in `state`.
      {
        datetime: "2026-01-01T02:00:00Z",
        value: "auto",
        valueType: "string",
        state: "auto",
      },
      {
        datetime: "2026-01-01T03:00:00Z",
        value: false,
        valueType: "boolean",
        state: false,
      },
      {
        datetime: "2026-01-01T04:00:00Z",
        value: "manual",
        valueType: "string",
        state: "manual",
      },
    ];
    const numericTimes = toSeries("PT001", raw).points.map((p) => p.t);
    const stateTimes = toStateSeries("PT001", raw).points.map((p) => p.t);

    expect(numericTimes).toEqual(["2026-01-01T01:00:00Z"]);
    expect(stateTimes).toEqual([
      "2026-01-01T02:00:00Z",
      "2026-01-01T03:00:00Z",
      "2026-01-01T04:00:00Z",
    ]);
    expect(numericTimes.filter((t) => stateTimes.includes(t))).toEqual([]);
  });

  // The one row that legitimately belongs to BOTH: a mixed aggregate bucket has a numeric average
  // (the chart's) and a state representative (the timeline's). Treating the two series as strict
  // complements would silently drop one of them — the chart lost the average before this was
  // separated out, and the timeline would have lost the state after #344. `state` (#359) is the
  // field that keeps the second half reachable now that valueText/valueBool are off the wire.
  it("keeps a mixed aggregate bucket in both series — it carries an average and a state", () => {
    const raw: TelemetryReading[] = [
      {
        datetime: "2026-01-01T01:00:00Z",
        value: 42,
        valueType: "number",
        state: "auto",
      },
    ];
    expect(toSeries("PT001", raw).points).toEqual([
      { t: "2026-01-01T01:00:00Z", v: 42 },
    ]);
    expect(toStateSeries("PT001", raw).points).toEqual([
      { t: "2026-01-01T01:00:00Z", state: "auto" },
    ]);
  });
});

describe("toStateSeries", () => {
  it("keeps only non-numeric rows, ascending, formatted (#152 Phase B)", () => {
    const raw = [
      {
        datetime: "2026-01-01T03:00:00Z",
        value: true,
        valueType: "boolean",
        state: true,
      },
      {
        datetime: "2026-01-01T01:00:00Z",
        value: "auto",
        valueType: "string",
        state: "auto",
      },
      { datetime: "2026-01-01T02:00:00Z", value: 42, valueType: "number" }, // dropped (numeric)
      { datetime: null, value: "x", valueType: "string", state: "x" }, // dropped (no datetime)
    ];
    const s = toStateSeries("PT001", raw);
    expect(s.pointId).toBe("PT001");
    expect(s.points).toEqual([
      { t: "2026-01-01T01:00:00Z", state: "auto" },
      { t: "2026-01-01T03:00:00Z", state: "ON" },
    ]);
  });

  it("returns an empty series for a purely numeric point", () => {
    const raw = [{ datetime: "2026-01-01T01:00:00Z", value: 1, valueType: "number" }];
    expect(toStateSeries("PT001", raw).points).toEqual([]);
  });
});

describe("toLatestSample", () => {
  it("returns null for an empty result set", () => {
    expect(toLatestSample([])).toBeNull();
  });

  it("resolves the last row's discriminated value", () => {
    expect(
      toLatestSample([
        { datetime: "2026-01-01T01:00:00Z", value: 1, valueType: "number" },
        { datetime: "2026-01-01T02:00:00Z", value: 21.5, valueType: "number" },
      ]),
    ).toEqual({ t: "2026-01-01T02:00:00Z", value: { kind: "number", value: 21.5 } });

    expect(
      toLatestSample([
        {
          datetime: "2026-01-01T02:00:00Z",
          value: "auto",
          valueType: "string",
          state: "auto",
        },
      ]),
    ).toEqual({
      t: "2026-01-01T02:00:00Z",
      value: { kind: "string", value: "auto" },
    });

    expect(
      toLatestSample([
        {
          datetime: "2026-01-01T02:00:00Z",
          value: false,
          valueType: "boolean",
          state: false,
        },
      ]),
    ).toEqual({
      t: "2026-01-01T02:00:00Z",
      value: { kind: "boolean", value: false },
    });
  });

  it("returns a none value for a row with nothing representable", () => {
    expect(toLatestSample([{ datetime: "2026-01-01T01:00:00Z" }])).toEqual({
      t: "2026-01-01T01:00:00Z",
      value: { kind: "none" },
    });
  });

  it("carries a null t when the row has no datetime", () => {
    expect(toLatestSample([{ value: 1, valueType: "number" }])).toEqual({
      t: null,
      value: { kind: "number", value: 1 },
    });
  });
});

describe("toPointsLastSeen", () => {
  it("maps each row to pointId + lastSeen, dropping rows without a pointId", () => {
    const rows: LatestSample[] = [
      {
        pointId: "PT001",
        datetime: "2026-01-01T01:00:00Z",
        value: 1,
        valueType: "number",
      },
      { datetime: "2026-01-01T02:00:00Z", value: 2 },
    ];
    expect(toPointsLastSeen(rows)).toEqual([
      { pointId: "PT001", lastSeen: "2026-01-01T01:00:00Z", value: 1 },
    ]);
  });

  it("keeps a numeric latest value for the alarm evaluator", () => {
    const rows: LatestSample[] = [
      {
        pointId: "PT001",
        datetime: "2026-01-01T01:00:00Z",
        value: 0,
        valueType: "number",
      },
    ];
    expect(toPointsLastSeen(rows)[0].value).toBe(0);
  });

  it("projects a string/boolean latest reading to a null value (numeric-only by design)", () => {
    const rows: LatestSample[] = [
      {
        pointId: "PT001",
        datetime: "2026-01-01T01:00:00Z",
        value: "auto",
        valueType: "string",
        state: "auto",
      },
      {
        pointId: "PT002",
        datetime: "2026-01-01T02:00:00Z",
        value: true,
        valueType: "boolean",
        state: true,
      },
    ];
    expect(toPointsLastSeen(rows)).toEqual([
      { pointId: "PT001", lastSeen: "2026-01-01T01:00:00Z", value: null },
      { pointId: "PT002", lastSeen: "2026-01-01T02:00:00Z", value: null },
    ]);
  });

  // batch-latest only ever returns RAW latest samples (granularity=Raw, latest=true), never
  // aggregate buckets, so a row here has exactly one reading — no average riding alongside a state.
  // The alarm evaluator compares numbers, so a `state` beside the value must not leak into it.
  it("ignores the state half when projecting the numeric value", () => {
    const rows: LatestSample[] = [
      {
        pointId: "PT001",
        datetime: "2026-01-01T01:00:00Z",
        value: 21.5,
        valueType: "number",
        state: "auto",
      },
    ];
    expect(toPointsLastSeen(rows)).toEqual([
      { pointId: "PT001", lastSeen: "2026-01-01T01:00:00Z", value: 21.5 },
    ]);
  });

  it("carries a null lastSeen for a point with no reading", () => {
    const rows: LatestSample[] = [{ pointId: "PT001", datetime: null }];
    expect(toPointsLastSeen(rows)).toEqual([
      { pointId: "PT001", lastSeen: null, value: null },
    ]);
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
