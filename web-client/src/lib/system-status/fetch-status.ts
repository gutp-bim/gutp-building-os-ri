import Cookies from "js-cookie";
import type { SystemStatus } from "./types";

const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";

/**
 * Fetches `GET /api/system/status`. The endpoint is admin/platform-gated server-side; the bearer
 * token mirrors the rest of the web client (OIDC access token cookie). Browsers must never scrape
 * `/metrics` directly — the API server aggregates and gates it (#144).
 */
export async function fetchSystemStatus(signal?: AbortSignal): Promise<SystemStatus> {
  const token = Cookies.get("oidc.access_token") || "";
  const res = await fetch(`${API_BASE_URL}/api/system/status`, {
    headers: { Authorization: `Bearer ${token}` },
    signal,
  });
  if (!res.ok) {
    throw new Error(`system status request failed: ${res.status}`);
  }
  return (await res.json()) as SystemStatus;
}
