import { ControlResult } from "@/lib/gen/point_control_pb";
import { describe, expect, it } from "vitest";
import { toFeedbackState } from "./use-control-execution";

describe("toFeedbackState (#637)", () => {
  it("maps each FEEDBACK_* result, keeping the value or reason", () => {
    expect(toFeedbackState(ControlResult.FEEDBACK_MATCHED, "24")).toEqual({
      kind: "matched",
      value: "24",
    });
    expect(toFeedbackState(ControlResult.FEEDBACK_MISMATCH, "22")).toEqual({
      kind: "mismatch",
      value: "22",
    });
    expect(toFeedbackState(ControlResult.FEEDBACK_TIMEOUT, "")).toEqual({
      kind: "timeout",
    });
    expect(
      toFeedbackState(ControlResult.FEEDBACK_UNAVAILABLE, "no_feedback_point"),
    ).toEqual({
      kind: "unavailable",
      reason: "no_feedback_point",
    });
  });

  it("an empty value is absent, and a non-feedback result is not a confirmation", () => {
    expect(toFeedbackState(ControlResult.FEEDBACK_MATCHED, " ")).toEqual({
      kind: "matched",
      value: undefined,
    });
    expect(toFeedbackState(ControlResult.SUCCESS, "")).toBeNull();
    expect(toFeedbackState(ControlResult.FAILED, "")).toBeNull();
  });
});
