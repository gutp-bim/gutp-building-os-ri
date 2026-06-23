import type { ValidTelemetryData } from "@/lib/infra/aspida-client/generated/@types";
import type { TelemetryPoint, TelemetrySeries } from "./types";

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
