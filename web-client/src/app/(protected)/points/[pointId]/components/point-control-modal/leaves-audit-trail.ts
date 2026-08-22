import type { ControlExecutionState } from "@/lib/infra/grpc-client/use-control-execution";

/**
 * Whether a control execution state means the server has an audit row for it, so the 制御履歴 panel
 * is worth refetching and worth linking to (#162).
 *
 * The row is opened by `PointController.Control` *before* the command is published (#333) — but only
 * once the request has got past authorization and value validation. So the status alone is not
 * enough: `failed` is produced both by the gRPC result stream (row exists) and by
 * `controlPostErrorResult` for a POST that was rejected outright (no row). `dispatched` carries that
 * distinction from the modal, which is the only place that knows how the result was reached.
 *
 * @param dispatched the POST reached the point where an audit row is opened — i.e. it returned 2xx,
 *   or 503 (gateway offline), where the server opens the row and closes it out as failed.
 */
export function leavesAuditTrail(
  state: ControlExecutionState,
  dispatched: boolean,
): boolean {
  switch (state.status) {
    // 503: PointController opens the row, fails to publish, then closes it as failed (#333) — so a
    // row exists even though the command never reached a gateway.
    case "gateway_offline":
      return true;
    // 403 is rejected before any row is created; refetching would only produce a pointless request
    // and the link would point at an unchanged list.
    case "permission_denied":
      return false;
    case "idle":
    case "executing":
      return false;
    // A timeout means we stopped waiting, not that the gateway did — the result may still land, so
    // the row is worth re-reading. A cancel is the same situation from the operator's side.
    case "success":
    case "failed":
    case "timeout":
    case "cancelled":
      return dispatched;
  }
}
