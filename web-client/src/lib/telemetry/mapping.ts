import type {
  TelemetryGranularity,
  ValidTelemetryData,
} from "@/lib/infra/aspida-client/generated/@types";
import type {
  Granularity,
  TelemetryPoint,
  TelemetrySeries,
  TelemetryStatePoint,
  TelemetryStateSeries,
} from "./types";
import { formatTelemetryValue, isNonNumericValue } from "./value";

// Backend enum ordinals (BuildingOS.Shared.Infrastructure.Telemetry.TelemetryGranularity):
// Raw = 0, Hour = 1, Day = 2. OpenAPI/aspida types this as a bare number, so the friendly
// lowercase names used across the UI are mapped to their ordinal here, once.
const GRANULARITY_ORDINAL: Record<Granularity, TelemetryGranularity> = {
  raw: 0,
  hour: 1,
  day: 2,
};

export function toGranularityParam(
  granularity: Granularity | undefined,
): TelemetryGranularity | undefined {
  return granularity === undefined
    ? undefined
    : GRANULARITY_ORDINAL[granularity];
}

/**
 * Pure conversion of raw telemetry rows into a clean, datetime-ascending series. Rows without a
 * datetime or value are dropped; a zero value is kept (it is a real reading, not "missing").
 */
export function toSeries(
  pointId: string,
  data: ValidTelemetryData[],
): TelemetrySeries {
  const points: TelemetryPoint[] = data
    .filter(
      (d): d is ValidTelemetryData & { datetime: string; value: number } =>
        typeof d.datetime === "string" && typeof d.value === "number",
    )
    .map((d) => ({ t: d.datetime, v: d.value }))
    .sort((a, b) => new Date(a.t).getTime() - new Date(b.t).getTime());

  return { pointId, points };
}

/**
 * Pure conversion of raw telemetry rows into a datetime-ascending NON-numeric state series (#152
 * Phase B). Numeric rows are dropped (they belong on the chart via {@link toSeries}); each string/
 * boolean row becomes a display `state` string (booleans → ON/OFF). Rows without a datetime are
 * dropped.
 */
export function toStateSeries(
  pointId: string,
  data: ValidTelemetryData[],
): TelemetryStateSeries {
  const points: TelemetryStatePoint[] = data
    .filter((d): d is ValidTelemetryData & { datetime: string } =>
      typeof d.datetime === "string" && isNonNumericValue(d),
    )
    .map((d) => ({ t: d.datetime, state: formatTelemetryValue(d) ?? "" }))
    .sort((a, b) => new Date(a.t).getTime() - new Date(b.t).getTime());

  return { pointId, points };
}
