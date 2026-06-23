/**
 * S5 API Read Path Performance
 * Tests building traversal, latest-value queries, and warm range queries.
 */

import http from "k6/http";
import { check, sleep } from "k6";
import { Trend, Rate } from "k6/metrics";

const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";
const VUS = parseInt(__ENV.VUS || "10", 10);
const DURATION = __ENV.DURATION || "15m";
const TEST_RUN_ID = __ENV.TEST_RUN_ID || "s5-default";
// Range query lookback window in hours (default 1h). Widen to cover older lake data when the warm-up
// load is skipped (e.g. measuring against an existing run's data).
const RANGE_LOOKBACK_HOURS = parseFloat(__ENV.RANGE_LOOKBACK_HOURS || "1");
// The latest-value (hot) path reads the NATS KV store; set SKIP_LATEST=true to measure only the warm
// range path (parquet) when the hot store is unavailable.
const SKIP_LATEST = (__ENV.SKIP_LATEST || "false") === "true";

const latestValueTrend = new Trend("latest_value_duration", true);
const rangeQueryTrend = new Trend("range_query_duration", true);
const errorRate = new Rate("error_rate");

export const options = {
  vus: VUS,
  duration: DURATION,
  thresholds: {
    latest_value_duration: ["p(95)<500"],
    range_query_duration: ["p(95)<2000"],
    http_req_failed: ["rate<0.001"],
    error_rate: ["rate<0.001"],
  },
  tags: {
    test_run_id: TEST_RUN_ID,
    scenario: "s5_api_read",
  },
};

const tags = { test_run_id: TEST_RUN_ID };

function getBuildings() {
  const res = http.get(`${BASE_URL}/buildings`, { tags });
  const ok = check(res, {
    "buildings status 200": (r) => r.status === 200,
    "buildings body is array": (r) => {
      try {
        return Array.isArray(JSON.parse(r.body));
      } catch {
        return false;
      }
    },
  });
  errorRate.add(!ok);
  return res;
}

// Unified read path (#214/#216): tier auto-selection over the Parquet lake. The deprecated per-tier
// /telemetries/hot and /telemetries/warm endpoints return empty in parquet mode (Oss stub), so S5
// exercises /telemetries/query — latest=true for the hot path, start/end for the warm range path.
function getLatestValue(pointId) {
  const url = `${BASE_URL}/telemetries/query?pointId=${encodeURIComponent(pointId)}&latest=true`;
  const res = http.get(url, { tags });
  latestValueTrend.add(res.timings.duration);
  const ok = check(res, {
    "latest query status 200": (r) => r.status === 200,
  });
  errorRate.add(!ok);
  return res;
}

function getRangeQuery(pointId, fromISO, toISO) {
  const url =
    `${BASE_URL}/telemetries/query` +
    `?pointId=${encodeURIComponent(pointId)}` +
    `&start=${encodeURIComponent(fromISO)}` +
    `&end=${encodeURIComponent(toISO)}`;
  const res = http.get(url, { tags });
  rangeQueryTrend.add(res.timings.duration);
  const ok = check(res, {
    "range query status 200": (r) => r.status === 200,
  });
  errorRate.add(!ok);
  return res;
}

// Shared state: point IDs discovered from buildings listing
// Default point IDs seeded by smoke test (overridden by setup() discovery)
let knownPointIds = __ENV.POINT_IDS
  ? __ENV.POINT_IDS.split(",")
  : [
      "perf-point-20260518-00000-000",
      "perf-point-20260518-00001-001",
      "perf-point-20260518-00002-002",
      "perf-point-20260518-00003-003",
      "perf-point-20260518-00004-004",
      "perf-point-20260518-00005-000",
      "perf-point-20260518-00006-001",
      "perf-point-20260518-00007-002",
      "perf-point-20260518-00008-000",
      "perf-point-20260518-00009-001",
    ];

export function setup() {
  const res = http.get(`${BASE_URL}/buildings`, { tags });
  if (res.status !== 200) {
    return { pointIds: knownPointIds };
  }
  try {
    const buildings = JSON.parse(res.body);
    if (Array.isArray(buildings) && buildings.length > 0) {
      // Use building IDs as seed for point queries (actual point discovery requires further traversal)
      return { pointIds: knownPointIds, buildingCount: buildings.length };
    }
  } catch (_) {}
  return { pointIds: knownPointIds };
}

export default function (data) {
  const pointIds = (data && data.pointIds) || knownPointIds;
  const pointId = pointIds[Math.floor(Math.random() * pointIds.length)];

  // 1. Building traversal
  getBuildings();
  sleep(0.1);

  // 2. Latest-value query (most critical path; hot KV — skippable when NATS is unavailable)
  if (!SKIP_LATEST) {
    getLatestValue(pointId);
    sleep(0.1);
  }

  // 3. Warm range query (last RANGE_LOOKBACK_HOURS)
  const now = new Date();
  const toISO = now.toISOString();
  const fromDate = new Date(now.getTime() - RANGE_LOOKBACK_HOURS * 60 * 60 * 1000);
  const fromISO = fromDate.toISOString();
  getRangeQuery(pointId, fromISO, toISO);

  sleep(0.5);
}
