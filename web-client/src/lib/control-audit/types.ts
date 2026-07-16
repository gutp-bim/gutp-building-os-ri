/**
 * Domain types for a point's control command history (#162). Decoupled from the API JSON shape so an
 * eventual Swagger/aspida wiring is absorbed in `mapping.ts` + `repository.ts` only.
 */

export type ControlAuditStatus = "success" | "failed" | "pending";

/** One recorded device-control command (`point_control_audit` row). */
export type ControlAuditEntry = {
  controlId: string;
  pointId: string | null;
  /** The command payload JSON as sent (e.g. `{"value":21.5}`). */
  request: string;
  status: ControlAuditStatus;
  /** ISO-8601 timestamps. `completedAt` is null while the command is still in flight. */
  createdAt: string;
  completedAt: string | null;
};
