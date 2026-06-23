import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { isTourCompleted } from "@/lib/onboarding/storage";
import { OnboardingTour } from "./onboarding-tour";

describe("OnboardingTour", () => {
  beforeEach(() => window.localStorage.clear());

  it("auto-opens on first visit for a role with steps", () => {
    render(<OnboardingTour role="operator" />);
    expect(screen.getByTestId("onboarding-tour")).toBeInTheDocument();
  });

  it("renders nothing for an unknown role (no steps)", () => {
    const { container } = render(<OnboardingTour role={null} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("does not auto-open when already completed", () => {
    window.localStorage.setItem("buildingos.onboarding.completed.v1", "1");
    const { container } = render(<OnboardingTour role="operator" />);
    expect(container).toBeEmptyDOMElement();
  });

  it("skipping marks completed and closes", () => {
    render(<OnboardingTour role="operator" />);
    fireEvent.click(screen.getByRole("button", { name: "スキップ" }));
    expect(isTourCompleted()).toBe(true);
    expect(screen.queryByTestId("onboarding-tour")).not.toBeInTheDocument();
  });

  it("advances through steps and finishes on 完了", () => {
    render(<OnboardingTour role="operator" />);
    // operator sees welcome → operator → finish (3 steps)
    expect(screen.getByTestId("tour-progress")).toHaveTextContent("1 / 3");
    fireEvent.click(screen.getByRole("button", { name: "次へ" }));
    expect(screen.getByTestId("tour-progress")).toHaveTextContent("2 / 3");
    fireEvent.click(screen.getByRole("button", { name: "次へ" }));
    expect(screen.getByRole("button", { name: "完了" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "完了" }));
    expect(isTourCompleted()).toBe(true);
    expect(screen.queryByTestId("onboarding-tour")).not.toBeInTheDocument();
  });
});
