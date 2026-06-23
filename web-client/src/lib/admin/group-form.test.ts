import { describe, expect, it } from "vitest";
import { validateGroupForm } from "./group-form";

describe("validateGroupForm (create)", () => {
  const opts = { requireId: true };

  it("accepts a well-formed id and name", () => {
    expect(validateGroupForm({ id: "hvac-team", name: "HVAC" }, opts)).toEqual({ ok: true });
  });

  it("rejects a missing id or name", () => {
    expect(validateGroupForm({ id: "", name: "HVAC" }, opts).ok).toBe(false);
    expect(validateGroupForm({ id: "x", name: "  " }, opts).ok).toBe(false);
  });

  it("rejects an id with disallowed characters", () => {
    const r = validateGroupForm({ id: "hvac team", name: "HVAC" }, opts);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.error).toContain("英数字");
  });

  it("trims surrounding whitespace before checking", () => {
    expect(validateGroupForm({ id: "  team-1  ", name: "  名前  " }, opts)).toEqual({ ok: true });
  });
});

describe("validateGroupForm (edit)", () => {
  const opts = { requireId: false };

  it("only requires a name and ignores the id", () => {
    expect(validateGroupForm({ name: "Renamed" }, opts)).toEqual({ ok: true });
    expect(validateGroupForm({ name: "" }, opts).ok).toBe(false);
  });
});
