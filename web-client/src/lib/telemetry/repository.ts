import { API_BASE_URL, authHeaders } from "@/lib/admin/http";
import { apiClient } from "@/lib/infra/aspida-client";
import type { PointLastSeen } from "./freshness";
import { toGranularityParam, toSeries } from "./mapping";
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
      granularity: toGranularityParam(q.granularity),
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

/**
 * Batch latest-sample fetch (#182): one `POST /telemetries/query/batch-latest` for many points,
 * replacing the per-point N+1 the freshness view used to do. Returns each point's last-seen ISO
 * timestamp (null = no data). Points the server omits (a non-admin cannot read them) simply do not
 * appear — the caller fills those as missing.
 *
 * Bespoke fetch because the endpoint is not yet in the Swagger/aspida schema; wiring it into the
 * generated client is a follow-up (mirrors the admin-endpoints note in CLAUDE.md).
 */
export async function latestTelemetryBatch(
  pointIds: string[],
): Promise<PointLastSeen[]> {
  if (pointIds.length === 0) return [];
  const res = await fetch(`${API_BASE_URL}/telemetries/query/batch-latest`, {
    method: "POST",
    headers: authHeaders(true),
    body: JSON.stringify({ pointIds }),
  });
  if (!res.ok) {
    throw new Error(`最新値の一括取得に失敗しました (${res.status})`);
  }
  const rows = (await res.json()) as { pointId: string; datetime: string | null }[];
  return rows.map((r) => ({ pointId: r.pointId, lastSeen: r.datetime ?? null }));
}
