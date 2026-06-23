import { API_BASE_URL, authHeaders, mutationError } from "./http";

export interface SparqlQueryResult {
  columns: string[];
  rows: Record<string, string>[];
  rowCount: number;
  truncated: boolean;
  elapsedMs: number;
}

export interface GatewayCollision {
  gatewayId: string;
  buildingCount: number;
}

export interface TwinImportPreview {
  tripleCount: number;
  gatewayCount: number;
  collisions: GatewayCollision[];
  valid: boolean;
}

export type TwinImportMode = "append" | "replace";

/** Pure: an import may be applied only when the preview reports no gateway_id collisions (#322). */
export function canApplyImport(preview: TwinImportPreview | null): boolean {
  return preview !== null && preview.valid && preview.collisions.length === 0;
}

/** Pure: short human summary of a preview for display. */
export function previewSummary(preview: TwinImportPreview): string {
  const base = `${preview.tripleCount} トリプル / ${preview.gatewayCount} ゲートウェイ`;
  return preview.collisions.length === 0
    ? `${base} — 検証 OK`
    : `${base} — gateway_id 重複 ${preview.collisions.length} 件`;
}

export async function runReadOnlySparql(query: string, maxRows = 200): Promise<SparqlQueryResult> {
  const res = await fetch(`${API_BASE_URL}/api/admin/twin/query`, {
    method: "POST",
    headers: authHeaders(true),
    body: JSON.stringify({ query, maxRows }),
  });
  if (!res.ok) throw await mutationError(res, "クエリの実行に失敗しました");
  return (await res.json()) as SparqlQueryResult;
}

export async function previewTwinImport(turtle: string): Promise<TwinImportPreview> {
  const res = await fetch(`${API_BASE_URL}/api/admin/twin/import/preview`, {
    method: "POST",
    headers: authHeaders(true),
    body: JSON.stringify({ turtle }),
  });
  if (!res.ok) throw await mutationError(res, "プレビューに失敗しました");
  return (await res.json()) as TwinImportPreview;
}

export async function applyTwinImport(turtle: string, mode: TwinImportMode): Promise<TwinImportPreview> {
  const res = await fetch(`${API_BASE_URL}/api/admin/twin/import/apply`, {
    method: "POST",
    headers: authHeaders(true),
    body: JSON.stringify({ turtle, mode }),
  });
  if (!res.ok) throw await mutationError(res, "適用に失敗しました");
  return (await res.json()) as TwinImportPreview;
}
