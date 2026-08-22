import { describe, expect, it } from "vitest";
import { leavesAuditTrail } from "./leaves-audit-trail";

describe("leavesAuditTrail", () => {
  it.each(["success", "failed", "timeout", "cancelled"] as const)(
    "treats a dispatched %s as leaving a row worth re-reading",
    (status) => {
      expect(leavesAuditTrail({ status } as never, true)).toBe(true);
    },
  );

  it.each(["failed", "timeout", "cancelled"] as const)(
    "treats an undispatched %s as leaving nothing",
    (status) => {
      // controlPostErrorResult also produces `failed` for a POST rejected outright (400 validation,
      // 404, 500, a network error) — those never reach the point where an audit row is opened, so
      // linking there would send the operator to an unchanged list.
      expect(leavesAuditTrail({ status } as never, false)).toBe(false);
    },
  );

  it("always counts gateway_offline: the 503 path opens the row and closes it as failed", () => {
    // Guards the coupling with PointController's gateway-offline branch (#333). The POST failed, so
    // `dispatched` is false, yet a row does exist.
    expect(leavesAuditTrail({ status: "gateway_offline" }, false)).toBe(true);
  });

  it("never counts permission_denied — a 403 is rejected before any row is created", () => {
    expect(leavesAuditTrail({ status: "permission_denied" }, false)).toBe(
      false,
    );
    expect(leavesAuditTrail({ status: "permission_denied" }, true)).toBe(false);
  });

  it("excludes idle and executing — nothing settled yet", () => {
    expect(leavesAuditTrail({ status: "idle" }, true)).toBe(false);
    expect(
      leavesAuditTrail(
        { status: "executing", controlId: "c1", elapsedSeconds: 1 },
        true,
      ),
    ).toBe(false);
  });
});
