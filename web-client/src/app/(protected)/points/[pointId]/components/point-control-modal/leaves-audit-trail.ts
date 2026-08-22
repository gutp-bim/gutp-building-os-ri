import type { ControlExecutionState } from "@/lib/infra/grpc-client/use-control-execution";

/**
 * Whether a control execution state means the server has (or will shortly have) an audit row for it,
 * so the 制御履歴 panel is worth refetching (#162).
 *
 * The audit row is opened by `PointController.Control` *before* the command is published (#333), so
 * anything that got past the POST leaves a trace — including a gateway-offline 503, which the server
 * closes out as failed rather than leaving pending.
 *
 * `permission_denied` is deliberately excluded: a 403 is rejected before any row is created, so
 * refetching would only produce a pointless request. `executing` is excluded because the row is
 * already visible as 実行中 by the time the panel next loads, and refetching mid-flight would race
 * the result write for no benefit — the terminal state triggers the refetch that matters.
 */
export function leavesAuditTrail(state: ControlExecutionState): boolean {
  switch (state.status) {
    case "success":
    case "failed":
    case "timeout":
    case "gateway_offline":
      return true;
    // A timeout means we stopped waiting, not that the gateway did — the result may still land, so
    // the row is worth re-reading. A cancel is the same situation from the operator's side.
    case "cancelled":
      return true;
    case "idle":
    case "executing":
    case "permission_denied":
      return false;
  }
}
