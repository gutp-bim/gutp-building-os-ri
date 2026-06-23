import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ResolvedTourStep } from "@/lib/onboarding/types";
import { TourStepView } from "./tour-step-view";

const step: ResolvedTourStep = { id: "s", title: "ようこそ", body: ["一行目", "二行目"] };

describe("TourStepView", () => {
  it("renders content and progress", () => {
    render(<TourStepView step={step} index={1} total={3} onBack={vi.fn()} onNext={vi.fn()} onSkip={vi.fn()} />);
    expect(screen.getByText("ようこそ")).toBeInTheDocument();
    expect(screen.getByText("一行目")).toBeInTheDocument();
    expect(screen.getByTestId("tour-progress")).toHaveTextContent("2 / 3");
  });

  it("disables 戻る on the first step", () => {
    render(<TourStepView step={step} index={0} total={3} onBack={vi.fn()} onNext={vi.fn()} onSkip={vi.fn()} />);
    expect(screen.getByRole("button", { name: "戻る" })).toBeDisabled();
  });

  it("shows 完了 on the last step and 次へ otherwise", () => {
    const { rerender } = render(
      <TourStepView step={step} index={0} total={3} onBack={vi.fn()} onNext={vi.fn()} onSkip={vi.fn()} />,
    );
    expect(screen.getByRole("button", { name: "次へ" })).toBeInTheDocument();
    rerender(
      <TourStepView step={step} index={2} total={3} onBack={vi.fn()} onNext={vi.fn()} onSkip={vi.fn()} />,
    );
    expect(screen.getByRole("button", { name: "完了" })).toBeInTheDocument();
  });

  it("wires skip/next/back callbacks", () => {
    const onNext = vi.fn();
    const onSkip = vi.fn();
    render(<TourStepView step={step} index={1} total={3} onBack={vi.fn()} onNext={onNext} onSkip={onSkip} />);
    fireEvent.click(screen.getByRole("button", { name: "次へ" }));
    fireEvent.click(screen.getByRole("button", { name: "スキップ" }));
    expect(onNext).toHaveBeenCalled();
    expect(onSkip).toHaveBeenCalled();
  });
});
