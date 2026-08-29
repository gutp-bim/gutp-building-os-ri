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

/** Which link of the building hierarchy is missing for an unreachable resource (#291). */
export type TwinOrphanReason = "no_device" | "no_room" | "no_building_path";

export interface TwinOrphanResource {
  resourceId: string;
  reason: TwinOrphanReason;
}

/** Why a writable point's bos: control schema could not be resolved to a usable shape (#336). */
export type ControlSchemaIssueReason = "missing_datatype" | "malformed_enum_labels";

export interface TwinControlSchemaIssue {
  pointId: string;
  reason: ControlSchemaIssueReason;
}

export interface TwinImportPreview {
  tripleCount: number;
  gatewayCount: number;
  collisions: GatewayCollision[];
  /** Total unreachable resources; `orphans` is a capped sample, so it may be shorter (#291). */
  orphanCount: number;
  orphans: TwinOrphanResource[];
  /**
   * Writable points with a missing/malformed bos: control schema (#336) — observation-only, never
   * affects `valid` or blocks apply (the control path itself fails open the same way).
   * `controlSchemaIssues` is a capped sample, so it may be shorter than `controlSchemaIssueCount`.
   */
  controlSchemaIssueCount: number;
  controlSchemaIssues: TwinControlSchemaIssue[];
  valid: boolean;
}

export type TwinImportMode = "append" | "replace";

const ORPHAN_REASON_LABELS: Record<TwinOrphanReason, string> = {
  no_device: "デバイス未接続",
  // 部屋（sbco:locatedIn）とフロア文字列（sbco:floor）のどちらも無い＝空間的な足がかりが皆無。
  no_room: "空間未接続（部屋・フロア未指定）",
  no_building_path: "フロア・建物へ到達不能",
};

/**
 * Pure: Japanese label for an orphan reason; an unknown value passes through as-is.
 * The reason is server-supplied, so membership is an own-property check — `in` would also match
 * inherited keys ("toString" etc.) and return a function where a string is declared.
 */
export function orphanReasonLabel(reason: string): string {
  return Object.hasOwn(ORPHAN_REASON_LABELS, reason)
    ? ORPHAN_REASON_LABELS[reason as TwinOrphanReason]
    : reason;
}

const CONTROL_SCHEMA_ISSUE_REASON_LABELS: Record<ControlSchemaIssueReason, string> = {
  missing_datatype: "制御スキーマ未設定（dataType 欠落）",
  malformed_enum_labels: "enumLabels が不正な JSON",
};

/** Pure: Japanese label for a control-schema-issue reason; an unknown value passes through as-is. */
export function controlSchemaIssueReasonLabel(reason: string): string {
  return Object.hasOwn(CONTROL_SCHEMA_ISSUE_REASON_LABELS, reason)
    ? CONTROL_SCHEMA_ISSUE_REASON_LABELS[reason as ControlSchemaIssueReason]
    : reason;
}

/**
 * Pure: an import may be applied only when the preview reports no gateway_id collisions (#322) and
 * no resources outside the building hierarchy — the latter waivable by an explicit override (#291).
 */
export function canApplyImport(preview: TwinImportPreview | null, allowOrphans = false): boolean {
  if (preview === null) return false;
  if (preview.collisions.length > 0) return false;
  return preview.orphanCount === 0 || allowOrphans;
}

/** Pure: short human summary of a preview for display. */
export function previewSummary(preview: TwinImportPreview): string {
  const base = `${preview.tripleCount} トリプル / ${preview.gatewayCount} ゲートウェイ`;
  const issues: string[] = [];
  if (preview.collisions.length > 0) issues.push(`gateway_id 重複 ${preview.collisions.length} 件`);
  if (preview.orphanCount > 0) issues.push(`階層未接続 ${preview.orphanCount} 件`);
  if (preview.controlSchemaIssueCount > 0) issues.push(`制御スキーマ不整合 ${preview.controlSchemaIssueCount} 件`);
  return issues.length === 0 ? `${base} — 検証 OK` : `${base} — ${issues.join(" / ")}`;
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

/**
 * 取込前の検証。mode は適用予定のものを渡す — 階層未接続の判定範囲が変わる（append は既存ツインと併合
 * した後、replace はこの TTL 単体、#291）ため、mode が違うとプレビューと適用の結果がずれる。
 */
export async function previewTwinImport(
  turtle: string,
  mode: TwinImportMode,
): Promise<TwinImportPreview> {
  try {
    return (await apiClient().api.admin.twin.import.preview.$post({
      body: { turtle, mode },
    })) as TwinImportPreview;
  } catch (e) {
    throw mutationError(e, "プレビューに失敗しました");
  }
}

export async function applyTwinImport(
  turtle: string,
  mode: TwinImportMode,
  allowOrphans = false,
): Promise<TwinImportPreview> {
  try {
    return (await apiClient().api.admin.twin.import.apply.$post({
      body: { turtle, mode, allowOrphans },
    })) as TwinImportPreview;
  } catch (e) {
    throw mutationError(e, "適用に失敗しました");
  }
}
