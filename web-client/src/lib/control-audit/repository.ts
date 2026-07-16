import { API_BASE_URL, authHeaders } from "@/lib/admin/http";
import { toControlAuditEntry } from "./mapping";
import type { ControlAuditEntry } from "./types";

/**
 * Fetch a point's control command history (#162), newest first.
 *
 * Uses the bespoke authenticated fetch (Keycloak bearer from the `oidc.access_token` cookie) because
 * the endpoint `GET /points/{pointId}/control-audit` is not yet in the Swagger/aspida schema; wiring
 * it into the generated client is a follow-up (mirrors the admin-endpoints note in CLAUDE.md). All
 * request/response shape handling stays here + `mapping.ts` so the UI is insulated from the API shape.
 */
export async function fetchControlAudit(
  pointId: string,
  limit = 50,
): Promise<ControlAuditEntry[]> {
  const url = `${API_BASE_URL}/points/${encodeURIComponent(pointId)}/control-audit?limit=${limit}`;
  const res = await fetch(url, { headers: authHeaders() });
  if (!res.ok) {
    throw new Error(`制御履歴の取得に失敗しました (${res.status})`);
  }
  const rows = (await res.json()) as Record<string, unknown>[];
  return rows.map(toControlAuditEntry);
}
