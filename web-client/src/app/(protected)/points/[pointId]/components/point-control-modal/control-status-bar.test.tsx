import type { ControlExecutionState } from "@/lib/infra/grpc-client/use-control-execution";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ControlStatusBar } from "./control-status-bar";

const bar = (state: ControlExecutionState) =>
  render(
    <ControlStatusBar state={state} onCancel={vi.fn()} onDismiss={vi.fn()} />,
  );

describe("ControlStatusBar confirmation (#637)", () => {
  it("shows the plain success when the server sends no confirmation", () => {
    bar({ status: "success" });
    expect(screen.getByText("制御が正常に完了しました")).toBeInTheDocument();
    expect(screen.queryByTestId(/control-feedback-/)).not.toBeInTheDocument();
  });

  it("shows confirming, then the outcome with the actual value", () => {
    const { rerender } = bar({
      status: "success",
      feedback: { kind: "confirming" },
    });
    expect(screen.getByTestId("control-feedback-confirming")).toHaveTextContent(
      "確認中",
    );

    rerender(
      <ControlStatusBar
        state={{
          status: "success",
          feedback: { kind: "mismatch", value: "22" },
        }}
        onCancel={vi.fn()}
        onDismiss={vi.fn()}
      />,
    );
    expect(screen.getByTestId("control-feedback-mismatch")).toHaveTextContent(
      "実際の値: 22",
    );
  });

  it("explains why a control was not confirmed", () => {
    bar({
      status: "success",
      feedback: { kind: "unavailable", reason: "no_feedback_point" },
    });
    expect(
      screen.getByTestId("control-feedback-unavailable"),
    ).toHaveTextContent("確認用ポイントが設定されていません");
  });
});
