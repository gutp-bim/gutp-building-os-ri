import Cookies from "js-cookie";
import type { SettingView } from "./types";

const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:8081";

function authHeaders(json = false): HeadersInit {
  const headers: Record<string, string> = {
    Authorization: `Bearer ${Cookies.get("oidc.access_token") || ""}`,
  };
  if (json) headers["Content-Type"] = "application/json";
  return headers;
}

async function mutationError(res: Response, fallback: string): Promise<Error> {
  let detail = "";
  try {
    detail = (await res.text()).trim();
  } catch {
    // ignore
  }
  return new Error(detail || `${fallback} (${res.status})`);
}

/** `GET /api/system/settings` — admin-gated effective settings (defaults merged with overrides). */
export async function fetchSettings(signal?: AbortSignal): Promise<SettingView[]> {
  const res = await fetch(`${API_BASE_URL}/api/system/settings`, { headers: authHeaders(), signal });
  if (!res.ok) throw new Error(`settings request failed: ${res.status}`);
  return (await res.json()) as SettingView[];
}

/** `PUT /api/system/settings/{key}` — updates a value (type-validated server-side). Returns the view. */
export async function updateSetting(key: string, value: string): Promise<SettingView> {
  const res = await fetch(`${API_BASE_URL}/api/system/settings/${encodeURIComponent(key)}`, {
    method: "PUT",
    headers: authHeaders(true),
    body: JSON.stringify({ value }),
  });
  if (!res.ok) throw await mutationError(res, "設定の更新に失敗しました");
  return (await res.json()) as SettingView;
}

/** `DELETE /api/system/settings/{key}` — resets a setting to its default (removes the override). */
export async function resetSetting(key: string): Promise<void> {
  const res = await fetch(`${API_BASE_URL}/api/system/settings/${encodeURIComponent(key)}`, {
    method: "DELETE",
    headers: authHeaders(),
  });
  if (!res.ok) throw await mutationError(res, "設定のリセットに失敗しました");
}
