/**
 * S9 Warm Parquet Lake KPI (#219)
 *
 * Exercises the read paths whose KPIs differ between WARM_STORE=timescale and WARM_STORE=parquet, so
 * the SAME load can be run against each mode and compared (PRD docs/oss-warm-parquet-lake.md §7):
 *   - latest (hot KV — must not regress between modes)
 *   - warm 24h / 1 point range
 *   - cold 7d / 1 point range
 *   - hour & day aggregate (aggregate-on-read in parquet mode)
 *   - cold multi-point (one scan per object in parquet mode)
 *
 * Run twice (once per mode, same VUS/DURATION/POINT_IDS) and diff the p95 trends:
 *   BASE_URL=http://localhost:5000 MODE=parquet   k6 run Tools/e2e-performance/k6/s9_warm_kpi.js
 *   BASE_URL=http://localhost:5000 MODE=timescale k6 run Tools/e2e-performance/k6/s9_warm_kpi.js
 */

import http from "k6/http";
import { check, sleep } from "k6";
import { Trend, Rate } from "k6/metrics";

const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";
const VUS = parseInt(__ENV.VUS || "10", 10);
const DURATION = __ENV.DURATION || "10m";
const MODE = __ENV.MODE || "unknown"; // "parquet" | "timescale" — tags the run for comparison
const TEST_RUN_ID = __ENV.TEST_RUN_ID || `s9-${MODE}`;

const latest = new Trend("kpi_latest_duration", true);
const warm24h = new Trend("kpi_warm_24h_duration", true);
const cold7d = new Trend("kpi_cold_7d_duration", true);
const aggHour = new Trend("kpi_agg_hour_duration", true);
const aggDay = new Trend("kpi_agg_day_duration", true);
const multiPoint = new Trend("kpi_multipoint_duration", true);
const errorRate = new Rate("error_rate");

export const options = {
  vus: VUS,
  duration: DURATION,
  thresholds: {
    // PRD §7 pass criteria (apply to both modes; parquet must not regress beyond these).
    kpi_latest_duration: ["p(95)<500"],
    kpi_warm_24h_duration: ["p(95)<2000"],
    kpi_cold_7d_duration: ["p(95)<5000"],
    kpi_agg_hour_duration: ["p(95)<3000"],
    kpi_agg_day_duration: ["p(95)<3000"],
    http_req_failed: ["rate<0.001"],
    error_rate: ["rate<0.001"],
  },
  tags: { test_run_id: TEST_RUN_ID, scenario: "s9_warm_kpi", warm_store: MODE },
};

const tags = { test_run_id: TEST_RUN_ID, warm_store: MODE };

const POINT_IDS = __ENV.POINT_IDS
  ? __ENV.POINT_IDS.split(",")
  : [
      "perf-point-20260518-00000-000",
      "perf-point-20260518-00001-001",
      "perf-point-20260518-00002-002",
      "perf-point-20260518-00003-003",
      "perf-point-20260518-00004-004",
    ];

function get(url, trend) {
  const res = http.get(url, { tags });
  // KPI gate: only count served (200) responses in the latency trend — a 404 (e.g. a point with no
  // data) must not pad the percentiles. Non-200 is counted as an error instead.
  const ok = res.status === 200;
  if (ok) trend.add(res.timings.duration);
  errorRate.add(!check(res, { "status 200": (r) => r.status === 200 }));
  return res;
}

function iso(offsetMs) {
  return new Date(Date.now() - offsetMs).toISOString();
}

export default function () {
  const p = POINT_IDS[Math.floor(Math.random() * POINT_IDS.length)];
  const enc = encodeURIComponent;
  const day = 24 * 60 * 60 * 1000;

  // Unified read path (#214/#216): tier auto-selection over the Parquet lake. The deprecated per-tier
  // /telemetries/{hot,warm,cold,cold-multi-point} endpoints return empty in parquet mode, so all KPI
  // probes go through /telemetries/query (latest / range / granularity).

  // latest value (hot KV; lake-latest fallback when KV cold)
  get(`${BASE_URL}/telemetries/query?pointId=${enc(p)}&latest=true`, latest);

  // warm 24h range
  get(`${BASE_URL}/telemetries/query?pointId=${enc(p)}&start=${enc(iso(day))}&end=${enc(iso(0))}`, warm24h);

  // cold 7d range (router selects the cold/lake tier)
  get(`${BASE_URL}/telemetries/query?pointId=${enc(p)}&start=${enc(iso(7 * day))}&end=${enc(iso(0))}`, cold7d);

  // hour & day aggregate (aggregate-on-read in parquet mode; continuous aggregate in timescale)
  get(`${BASE_URL}/telemetries/query?pointId=${enc(p)}&start=${enc(iso(7 * day))}&end=${enc(iso(0))}&granularity=Hour`, aggHour);
  get(`${BASE_URL}/telemetries/query?pointId=${enc(p)}&start=${enc(iso(30 * day))}&end=${enc(iso(0))}&granularity=Day`, aggDay);

  // multi-point: /telemetries/query is single-point, so probe 3 points and record the total wall time
  // (parquet reads them in one lake scan server-side via QueryMultiAsync where wired; here we sum the
  // per-point query cost as an upper bound for the multi-point scenario).
  const t0 = Date.now();
  let multiOk = true;
  for (const x of POINT_IDS.slice(0, 3)) {
    const r = http.get(`${BASE_URL}/telemetries/query?pointId=${enc(x)}&start=${enc(iso(day))}&end=${enc(iso(0))}`, { tags });
    if (r.status !== 200) multiOk = false;
  }
  if (multiOk) multiPoint.add(Date.now() - t0);
  errorRate.add(!multiOk);

  sleep(0.5);
}
