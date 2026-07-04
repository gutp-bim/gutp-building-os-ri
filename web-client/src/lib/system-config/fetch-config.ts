import Cookies from "js-cookie";
import type { EffectiveConfig } from "./types";

const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";

/**
 * Fetches `GET /api/system/config`. Admin/platform-gated server-side; secrets are masked server-side
 * (their value is never sent). The bearer token mirrors the rest of the web client (OIDC cookie).
 */
export async function fetchEffectiveConfig(signal?: AbortSignal): Promise<EffectiveConfig> {
  const token = Cookies.get("oidc.access_token") || "";
  const res = await fetch(`${API_BASE_URL}/api/system/config`, {
    headers: { Authorization: `Bearer ${token}` },
    signal,
  });
  if (!res.ok) {
    throw new Error(`system config request failed: ${res.status}`);
  }
  return (await res.json()) as EffectiveConfig;
}
