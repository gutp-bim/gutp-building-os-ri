import { describe, expect, it } from "vitest";
import {
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
    });
    expect(entry).toEqual({
      controlId: "c1",
      pointId: "PT001",
      request: '{"value":21.5}',
      status: "success",
      createdAt: "2026-07-15T00:00:00Z",
      completedAt: "2026-07-15T00:00:01Z",
    });
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
