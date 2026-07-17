import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { InlineBanner } from "./inline-banner";

describe("InlineBanner", () => {
  it("is assertive (role=alert) for errors and polite (role=status) otherwise", () => {
    const { rerender } = render(<InlineBanner tone="error">boom</InlineBanner>);
    expect(screen.getByRole("alert")).toHaveTextContent("boom");
    rerender(<InlineBanner tone="success">done</InlineBanner>);
    expect(screen.getByRole("status")).toHaveTextContent("done");
  });

  it("calls onDismiss when the dismiss control is clicked", () => {
    const onDismiss = vi.fn();
    render(
      <InlineBanner tone="info" onDismiss={onDismiss}>
        hi
      </InlineBanner>,
    );
    fireEvent.click(screen.getByLabelText("閉じる"));
    expect(onDismiss).toHaveBeenCalledOnce();
  });

  it("omits the dismiss control when no handler is given", () => {
    render(<InlineBanner tone="error">no dismiss</InlineBanner>);
    expect(screen.queryByLabelText("閉じる")).not.toBeInTheDocument();
  });
});
