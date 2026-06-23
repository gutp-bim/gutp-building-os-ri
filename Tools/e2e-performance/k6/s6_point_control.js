/**
 * S6 Point Control E2E
 *
 * Prerequisites:
 *   - API Server running with DISABLE_AUTH=true
 *   - ConnectorWorker running with ENABLE_SIM_CONTROL=true
 *   - CONTROL_POINT_ID set to a point that exists in OxiGraph (writable)
 *
 * Environment variables:
 *   BASE_URL          — API Server base URL (default: http://localhost:5000)
 *   CONTROL_POINT_ID  — Writable point ID registered in OxiGraph
 *   VUS               — Virtual users (default: 3)
 *   DURATION          — Test duration (default: 3m)
 *   TEST_RUN_ID       — Run identifier
 *
 * Phases tested (current contract: POST /points/{id}/control { "value": <number> }):
 *   A — Valid submission → 202 Accepted + controlId
 *   B — Non-existent point → 404 (point not found)
 *   C — Missing value → 400 bad request
 */

import http from "k6/http";
import { check, sleep, group } from "k6";
import { Rate, Trend } from "k6/metrics";

const BASE_URL = __ENV.BASE_URL || "http://localhost:5000";
const CONTROL_POINT_ID = __ENV.CONTROL_POINT_ID || "sim-control-point-001";
const VUS = parseInt(__ENV.VUS || "3", 10);
const DURATION = __ENV.DURATION || "3m";
const TEST_RUN_ID = __ENV.TEST_RUN_ID || "s6-default";

const submissionLatency = new Trend("control_submission_duration", true);
const errorRate = new Rate("s6_error_rate");
const timeoutRate = new Rate("timeout_rate");

export const options = {
  vus: VUS,
  duration: DURATION,
  thresholds: {
    // NOTE: no http_req_failed threshold — phases B (404) and C (400) intentionally exercise error
    // responses, which k6 counts as "failed" HTTP requests. Real failures are tracked by s6_error_rate
    // (which scores the per-phase status-code checks), so that is the meaningful gate.
    control_submission_duration: ["p(95)<3000"],
    s6_error_rate: ["rate<0.01"],
    timeout_rate: ["rate<0.01"],
  },
  tags: {
    test_run_id: TEST_RUN_ID,
    scenario: "s6_point_control",
  },
};

const commonParams = {
  headers: { "Content-Type": "application/json" },
  timeout: "10s",
  tags: { test_run_id: TEST_RUN_ID },
};

// Current control contract: PointController.Control binds { value: double }. The egress ControlType is
// resolved server-side from the point's gateway binding (not sent by the client).
function buildPayload(value) {
  return JSON.stringify({ value });
}

export default function () {
  // Phase A: Valid control submission
  group("phase_a_valid_submission", () => {
    const url = `${BASE_URL}/points/${encodeURIComponent(CONTROL_POINT_ID)}/control`;
    const payload = buildPayload(Math.round(Math.random() * 1000) / 10);

    const res = http.post(url, payload, commonParams);
    submissionLatency.add(res.timings.duration);

    const isTimeout = res.status === 0 || res.timings.duration >= 9900;
    timeoutRate.add(isTimeout);

    const ok = check(res, {
      "A: 202 Accepted": (r) => r.status === 202,
      "A: response has controlId": (r) => {
        if (r.status !== 202) return true;
        try {
          const body = JSON.parse(r.body);
          return typeof body.controlId === "string" && body.controlId.length > 0;
        } catch {
          return false;
        }
      },
      "A: no timeout": (_r) => !isTimeout,
    });
    errorRate.add(!ok);
  });

  sleep(0.5);

  // Phase B: Non-existent point → expect 404
  group("phase_b_not_found", () => {
    const url = `${BASE_URL}/points/nonexistent-s6-test-point-000/control`;
    const payload = buildPayload(0);

    const res = http.post(url, payload, commonParams);

    const isTimeout = res.status === 0 || res.timings.duration >= 9900;
    timeoutRate.add(isTimeout);

    const ok = check(res, {
      "B: nonexistent point 404 or 403": (r) => r.status === 404 || r.status === 403,
      "B: no timeout": (_r) => !isTimeout,
    });
    errorRate.add(!ok);
  });

  sleep(0.5);

  // Phase C: Missing value → expect 400
  group("phase_c_bad_request", () => {
    const url = `${BASE_URL}/points/${encodeURIComponent(CONTROL_POINT_ID)}/control`;
    const payload = JSON.stringify({}); // no value → 400 "value is required"

    const res = http.post(url, payload, commonParams);

    const isTimeout = res.status === 0 || res.timings.duration >= 9900;
    timeoutRate.add(isTimeout);

    const ok = check(res, {
      "C: missing value 400": (r) => r.status === 400,
      "C: no timeout": (_r) => !isTimeout,
    });
    errorRate.add(!ok);
  });

  sleep(1);
}
