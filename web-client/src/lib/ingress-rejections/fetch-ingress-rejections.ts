import Cookies from "js-cookie";
import type { IngressRejectionStats } from "./types";

const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";

/**
 * Fetches `GET /api/system/ingress-rejections`. The endpoint is admin-gated server-side; the
 * bearer token mirrors the rest of the web client (OIDC access token cookie).
 */
export async function fetchIngressRejectionStats(
  signal?: AbortSignal,
): Promise<IngressRejectionStats> {
  const token = Cookies.get("oidc.access_token") || "";
  const res = await fetch(`${API_BASE_URL}/api/system/ingress-rejections`, {
    headers: { Authorization: `Bearer ${token}` },
    signal,
  });
  if (!res.ok) {
    throw new Error(`ingress rejections request failed: ${res.status}`);
  }
  return (await res.json()) as IngressRejectionStats;
}
