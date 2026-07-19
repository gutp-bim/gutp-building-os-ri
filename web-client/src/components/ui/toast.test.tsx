import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ToastProvider, useToast } from "./toast";

function Trigger({ durationMs }: { durationMs?: number }) {
  const { showToast } = useToast();
  return (
    <button
      onClick={() => showToast("保存しました", { tone: "success", durationMs })}
    >
      go
    </button>
  );
}

describe("ToastProvider / useToast", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("shows a toast, then auto-dismisses after the duration", () => {
    render(
      <ToastProvider>
        <Trigger durationMs={5000} />
      </ToastProvider>,
    );
    act(() => {
      fireEvent.click(screen.getByText("go"));
    });
    expect(screen.getByTestId("toast-success")).toHaveTextContent(
      "保存しました",
    );

    act(() => {
      vi.advanceTimersByTime(5000);
    });
    expect(screen.queryByTestId("toast-success")).toBeNull();
  });

  it("stays until dismissed when durationMs is 0, then closes on the dismiss button", () => {
    render(
      <ToastProvider>
        <Trigger durationMs={0} />
      </ToastProvider>,
    );
    act(() => {
      fireEvent.click(screen.getByText("go"));
    });
    const toast = screen.getByTestId("toast-success");
    // No auto-dismiss.
    act(() => {
      vi.advanceTimersByTime(60_000);
    });
    expect(screen.getByTestId("toast-success")).toBeInTheDocument();

    act(() => {
      fireEvent.click(within(toast).getByLabelText("閉じる"));
    });
    expect(screen.queryByTestId("toast-success")).toBeNull();
  });

  it("throws when useToast is used without a provider", () => {
    // Suppress React's expected error log for the throwing render.
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    expect(() => render(<Trigger />)).toThrow(/ToastProvider/);
    spy.mockRestore();
  });
});
