import { describe, expect, it } from "vitest";
import type { HelpEntry } from "@/lib/help/types";
import { resolveTourStep, resolvedStepsForRole, stepsForRole } from "./select";
import type { TourStep } from "./types";

const steps: TourStep[] = [
  { id: "welcome", roles: ["admin", "operator", "viewer"], title: "W", body: ["hi"] },
  { id: "admin-only", roles: ["admin"], helpKey: "platform.status" },
];

const fakeHelp = (key: string): HelpEntry | null =>
  key === "platform.status" ? { key, title: "稼働状態", body: ["p1", "p2"] } : null;

describe("stepsForRole", () => {
  it("filters by role and returns [] for null", () => {
    expect(stepsForRole("operator", steps).map((s) => s.id)).toEqual(["welcome"]);
    expect(stepsForRole("admin", steps).map((s) => s.id)).toEqual(["welcome", "admin-only"]);
    expect(stepsForRole(null, steps)).toEqual([]);
  });

  it("admins see admin-only platform steps from the default content", () => {
    expect(stepsForRole("admin").some((s) => s.id === "platform-settings")).toBe(true);
    expect(stepsForRole("operator").some((s) => s.id === "platform-settings")).toBe(false);
  });
});

describe("resolveTourStep", () => {
  it("reuses D-1 help content when helpKey is set", () => {
    const r = resolveTourStep(steps[1], fakeHelp);
    expect(r.title).toBe("稼働状態");
    expect(r.body).toEqual(["p1", "p2"]);
  });

  it("uses inline content when there is no helpKey", () => {
    expect(resolveTourStep(steps[0], fakeHelp)).toEqual({ id: "welcome", title: "W", body: ["hi"] });
  });

  it("falls back to id/empty when helpKey is unknown and no inline content", () => {
    const r = resolveTourStep({ id: "x", roles: ["admin"], helpKey: "missing" }, fakeHelp);
    expect(r).toEqual({ id: "x", title: "x", body: [] });
  });
});

describe("resolvedStepsForRole", () => {
  it("filters then resolves", () => {
    const r = resolvedStepsForRole("admin", steps, fakeHelp);
    expect(r.map((s) => s.title)).toEqual(["W", "稼働状態"]);
  });
});
