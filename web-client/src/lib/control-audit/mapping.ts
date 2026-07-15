import type { ControlAuditEntry, ControlAuditStatus } from "./types";

const KNOWN_STATUSES: readonly ControlAuditStatus[] = ["success", "failed", "pending"];

function normalizeStatus(raw: unknown): ControlAuditStatus {
  return typeof raw === "string" && (KNOWN_STATUSES as readonly string[]).includes(raw)
    ? (raw as ControlAuditStatus)
    : "pending";
}

/**
 * Map one raw API row (camelCase JSON from `GET /points/{id}/control-audit`) to the domain type,
 * normalizing the status and nullable fields. Pure and unit-tested.
 */
export function toControlAuditEntry(raw: Record<string, unknown>): ControlAuditEntry {
  return {
    controlId: String(raw.controlId ?? ""),
    pointId: typeof raw.pointId === "string" ? raw.pointId : null,
    request: typeof raw.request === "string" ? raw.request : "",
    status: normalizeStatus(raw.status),
    createdAt: String(raw.createdAt ?? ""),
    completedAt: typeof raw.completedAt === "string" ? raw.completedAt : null,
  };
}

/** Japanese label for a control status. */
export function controlStatusLabel(status: ControlAuditStatus): string {
  switch (status) {
    case "success":
      return "成功";
    case "failed":
      return "失敗";
    case "pending":
      return "実行中";
  }
}

/**
 * Best-effort human display of the command request payload: `{"value":21.5}` → `値 21.5`. Falls back
 * to the raw string when it is not a JSON object carrying a `value`.
 */
export function formatControlRequest(request: string): string {
  try {
    const parsed: unknown = JSON.parse(request);
    if (parsed && typeof parsed === "object" && "value" in parsed) {
      return `値 ${String((parsed as { value: unknown }).value)}`;
    }
  } catch {
    // not JSON — show as-is
  }
  return request;
}
