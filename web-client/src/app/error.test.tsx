import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import ErrorBoundary from "./error";

describe("global Error boundary (#190)", () => {
  it("shows a Japanese recovery screen with retry and /home", () => {
    const reset = vi.fn();
    render(<ErrorBoundary error={new Error("boom")} reset={reset} />);
    expect(screen.getByText("エラーが発生しました")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "もう一度試す" }));
    expect(reset).toHaveBeenCalledOnce();
    expect(screen.getByRole("link", { name: /ホーム/ })).toHaveAttribute(
      "href",
      "/home",
    );
  });
});
