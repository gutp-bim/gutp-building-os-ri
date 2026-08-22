import { describe, expect, it } from "vitest";
import { leavesAuditTrail } from "./leaves-audit-trail";

describe("leavesAuditTrail", () => {
  it.each([
    "success",
    "failed",
    "timeout",
    "gateway_offline",
    "cancelled",
  ] as const)("treats %s as leaving a row worth re-reading", (status) => {
    expect(leavesAuditTrail({ status } as never)).toBe(true);
  });

  it("excludes permission_denied — a 403 is rejected before any row is created", () => {
    expect(leavesAuditTrail({ status: "permission_denied" })).toBe(false);
  });

  it("excludes idle and executing — nothing settled yet", () => {
    expect(leavesAuditTrail({ status: "idle" })).toBe(false);
    expect(
      leavesAuditTrail({
        status: "executing",
        controlId: "c1",
        elapsedSeconds: 1,
      }),
    ).toBe(false);
  });

  it("includes gateway_offline: the 503 path closes the row out as failed, so it exists", () => {
    // Guards the coupling with PointController's gateway-offline branch (#333): if that stopped
    // opening/closing a row, linking the operator to an unchanged history would be misleading.
    expect(leavesAuditTrail({ status: "gateway_offline" })).toBe(true);
  });
});
