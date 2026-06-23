import { apiClient } from "@/lib/infra/aspida-client";
import { toSeries } from "./mapping";
import type { TelemetryPoint, TelemetryQuery, TelemetrySeries } from "./types";

/**
 * Telemetry access façade. Everything routes through `/telemetries/query` (tier auto-selection +
 * granularity + latest), so callers never choose between hot/warm/cold. An API change is absorbed
 * here, not in the UI.
 */

export async function queryTelemetry(
  q: TelemetryQuery,
  token?: string,
): Promise<TelemetrySeries> {
  const res = await apiClient(token).telemetries.query.$get({
    query: {
      pointId: q.pointId,
      start: q.start?.toISOString(),
      end: q.end?.toISOString(),
      granularity: q.granularity,
      latest: q.latest,
    },
  });
  return toSeries(q.pointId, res);
}

/** Latest single sample for a point, or null when there is no data. */
export async function latestTelemetry(
  pointId: string,
  token?: string,
): Promise<TelemetryPoint | null> {
  const series = await queryTelemetry({ pointId, latest: true }, token);
  return series.points.at(-1) ?? null;
}
