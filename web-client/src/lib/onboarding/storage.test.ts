import { beforeEach, describe, expect, it } from "vitest";
import { isTourCompleted, markTourCompleted, resetTour } from "./storage";

describe("onboarding storage", () => {
  beforeEach(() => window.localStorage.clear());

  it("defaults to not completed", () => {
    expect(isTourCompleted()).toBe(false);
  });

  it("marks completed and reads it back", () => {
    markTourCompleted();
    expect(isTourCompleted()).toBe(true);
  });

  it("reset clears completion", () => {
    markTourCompleted();
    resetTour();
    expect(isTourCompleted()).toBe(false);
  });
});
