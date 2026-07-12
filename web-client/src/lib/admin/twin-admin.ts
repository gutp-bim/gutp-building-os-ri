import { apiClient } from "@/lib/infra/aspida-client";
import { mutationError } from "./api-error";

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
  try {
    return (await apiClient().api.admin.twin.query.$post({
      body: { query, maxRows },
    })) as SparqlQueryResult;
  } catch (e) {
    throw mutationError(e, "クエリの実行に失敗しました");
  }
}

export async function previewTwinImport(turtle: string): Promise<TwinImportPreview> {
  try {
    return (await apiClient().api.admin.twin.import.preview.$post({
      body: { turtle },
    })) as TwinImportPreview;
  } catch (e) {
    throw mutationError(e, "プレビューに失敗しました");
  }
}

export async function applyTwinImport(turtle: string, mode: TwinImportMode): Promise<TwinImportPreview> {
  try {
    return (await apiClient().api.admin.twin.import.apply.$post({
      body: { turtle, mode },
    })) as TwinImportPreview;
  } catch (e) {
    throw mutationError(e, "適用に失敗しました");
  }
}
