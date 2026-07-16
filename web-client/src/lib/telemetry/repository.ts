import Cookies from "js-cookie";
import { apiClient } from "@/lib/infra/aspida-client";
import { OIDC_TOKEN_COOKIE } from "@/lib/auth/oidc-config";
import type { PointLastSeen } from "./freshness";
import { toGranularityParam, toSeries } from "./mapping";
import type { TelemetryPoint, TelemetryQuery, TelemetrySeries } from "./types";

/**
 * Telemetry access façade. Everything routes through `/telemetries/query` (tier auto-selection +
 * granularity + latest), so callers never choose between hot/warm/cold. An API change is absorbed
 * here, not in the UI.
 */

/**
 * Base URL + bearer for the one telemetry endpoint not yet in the aspida schema (`batch-latest`).
 * Kept module-local (Keycloak bearer from the `oidc.access_token` cookie) rather than importing the
 * `@/lib/admin/http` helpers — those are for admin endpoints, and telemetry is not an admin surface.
 * This mirrors the other bespoke non-admin fetchers (`system-config`, `assistant`, …). Migrating the
 * endpoint into the generated client (Swagger + `sync-type`) is the tracked follow-up.
 */
const TELEMETRY_API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";

function telemetryJsonHeaders(): HeadersInit {
  return {
    "Content-Type": "application/json",
    Authorization: `Bearer ${Cookies.get(OIDC_TOKEN_COOKIE) || ""}`,
  };
}

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
 *
 * Bespoke fetch (module-local base URL/bearer above) because the endpoint is not yet in the
 * Swagger/aspida schema; wiring it into the generated client is the tracked follow-up.
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
  const res = await fetch(`${TELEMETRY_API_BASE_URL}/telemetries/query/batch-latest`, {
    method: "POST",
    headers: telemetryJsonHeaders(),
    body: JSON.stringify({ pointIds }),
  });
  if (!res.ok) {
    throw new Error(`最新値の一括取得に失敗しました (${res.status})`);
  }
  const rows = (await res.json()) as { pointId: string; datetime: string | null }[];
  return rows.map((r) => ({ pointId: r.pointId, lastSeen: r.datetime ?? null }));
}
