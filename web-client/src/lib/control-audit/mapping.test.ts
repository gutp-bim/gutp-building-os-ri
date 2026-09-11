import { describe, expect, it } from "vitest";
import {
  controlActorLabel,
  controlStatusLabel,
  formatControlRequest,
  toControlAuditEntry,
} from "./mapping";

describe("toControlAuditEntry", () => {
  it("maps a well-formed row", () => {
    const entry = toControlAuditEntry({
      controlId: "c1",
      pointId: "PT001",
      request: '{"value":21.5}',
      status: "success",
      createdAt: "2026-07-15T00:00:00Z",
      completedAt: "2026-07-15T00:00:01Z",
      actorSub: "kc-sub-42",
      actorName: "Yamada",
    });
    expect(entry).toEqual({
      controlId: "c1",
      pointId: "PT001",
      request: '{"value":21.5}',
      status: "success",
      createdAt: "2026-07-15T00:00:00Z",
      completedAt: "2026-07-15T00:00:01Z",
      actorSub: "kc-sub-42",
      actorName: "Yamada",
    });
  });

  it("falls back to the unknown sentinel for a row written before the actor column (#461)", () => {
    // Rows the migration backfilled, and any server that predates the column, carry no usable
    // actor. Reading that as an empty name would render a blank cell that looks like a UI bug.
    const entry = toControlAuditEntry({ controlId: "c9", status: "success" });
    expect(entry.actorSub).toBe("unknown");
    expect(entry.actorName).toBeNull();
  });

  it("normalizes an unknown/absent status to pending and nulls missing fields", () => {
    const entry = toControlAuditEntry({ controlId: "c2", status: "weird" });
    expect(entry.status).toBe("pending");
    expect(entry.pointId).toBeNull();
    expect(entry.completedAt).toBeNull();
    expect(entry.request).toBe("");
  });

  it("keeps a null completedAt for an in-flight command", () => {
    const entry = toControlAuditEntry({
      controlId: "c3",
      status: "pending",
      completedAt: null,
    });
    expect(entry.status).toBe("pending");
    expect(entry.completedAt).toBeNull();
  });
});

describe("controlActorLabel", () => {
  it("prefers the display name when the server has one", () => {
    expect(controlActorLabel({ actorSub: "kc-sub-42", actorName: "Yamada" })).toBe("Yamada");
  });

  it("falls back to the subject identifier, which is what correlates with admin_audit", () => {
    expect(controlActorLabel({ actorSub: "kc-sub-42", actorName: null })).toBe("kc-sub-42");
  });

  it("reads the unknown sentinel as 不明 rather than showing it as a user id", () => {
    expect(controlActorLabel({ actorSub: "unknown", actorName: null })).toBe("不明");
  });
});

describe("controlStatusLabel", () => {
  it("gives a Japanese label per status", () => {
    expect(controlStatusLabel("success")).toBe("成功");
    expect(controlStatusLabel("failed")).toBe("失敗");
    expect(controlStatusLabel("pending")).toBe("実行中");
  });
});

describe("formatControlRequest", () => {
  it("extracts the value from a JSON command payload", () => {
    expect(formatControlRequest('{"value":21.5}')).toBe("値 21.5");
  });

  it("falls back to the raw string when not a value-carrying JSON object", () => {
    expect(formatControlRequest("not json")).toBe("not json");
    expect(formatControlRequest('{"other":1}')).toBe('{"other":1}');
  });
});
