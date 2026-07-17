import { apiClient } from "@/lib/infra/aspida-client";
import type { PointLastSeen } from "./freshness";
import { toGranularityParam, toSeries } from "./mapping";
import type { TelemetryPoint, TelemetryQuery, TelemetrySeries } from "./types";

/**
 * Telemetry access façade. Everything routes through the generated Aspida client (`apiClient()`),
 * so callers never choose between hot/warm/cold and an API/Swagger change is absorbed here, not in
 * the UI. The `/telemetries/query` read auto-selects the tier (granularity + latest).
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
 * Server-side cap on a single `batch-latest` request (mirrors `TelemetryController.MaxBatchPointIds`,
 * #182). Requests above this are rejected 400 server-side, so the client splits into chunks of this
 * size — a floor with more points than the cap must not degrade to "all missing".
 */
export const MAX_BATCH_POINT_IDS = 500;

/**
 * Batch latest-sample fetch (#182): `POST /telemetries/query/batch-latest` for many points, replacing
 * the per-point N+1 the freshness view used to do. Returns each point's last-seen ISO timestamp
 * (null = no data). Points the server omits (a non-admin cannot read them) simply do not appear — the
 * caller fills those as missing.
 *
 * More than {@link MAX_BATCH_POINT_IDS} ids are split into that-sized chunks (the endpoint's cap) and
 * the results merged, so a large floor still resolves instead of tripping the server's 400 (#182
 * review). A failure in any chunk rejects the whole call so the caller surfaces an error rather than
 * mistaking the request-limit for genuinely-missing data.
 */
export async function latestTelemetryBatch(
  pointIds: string[],
): Promise<PointLastSeen[]> {
  if (pointIds.length === 0) return [];

  const chunks: string[][] = [];
  for (let i = 0; i < pointIds.length; i += MAX_BATCH_POINT_IDS) {
    chunks.push(pointIds.slice(i, i + MAX_BATCH_POINT_IDS));
  }

  const results = await Promise.all(chunks.map(fetchLatestBatchChunk));
  return results.flat();
}

async function fetchLatestBatchChunk(pointIds: string[]): Promise<PointLastSeen[]> {
  let rows: { pointId?: string; datetime?: string | null }[];
  try {
    rows = await apiClient().telemetries.query.batch_latest.$post({
      body: { pointIds },
    });
  } catch (e) {
    const status = httpStatusOf(e);
    throw new Error(
      `最新値の一括取得に失敗しました${status !== undefined ? ` (${status})` : ""}`,
    );
  }
  return rows
    .filter((r): r is { pointId: string; datetime?: string | null } =>
      typeof r.pointId === "string",
    )
    .map((r) => ({ pointId: r.pointId, lastSeen: r.datetime ?? null }));
}

/** Best-effort HTTP status from an Aspida/axios rejection, for a friendlier error message. */
function httpStatusOf(e: unknown): number | undefined {
  if (e && typeof e === "object" && "response" in e) {
    const status = (e as { response?: { status?: unknown } }).response?.status;
    if (typeof status === "number") return status;
  }
  return undefined;
}
