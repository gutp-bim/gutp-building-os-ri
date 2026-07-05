import type {
  TelemetryGranularity,
  ValidTelemetryData,
} from "@/lib/infra/aspida-client/generated/@types";
import type { Granularity, TelemetryPoint, TelemetrySeries } from "./types";

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
