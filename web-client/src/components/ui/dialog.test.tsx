import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { Dialog } from "./dialog";

describe("Dialog (#194)", () => {
  it("renders nothing when closed", () => {
    const { container } = render(
      <Dialog open={false} onClose={vi.fn()}>
        <p>本文</p>
      </Dialog>,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("exposes modal dialog semantics and an accessible name", () => {
    render(
      <Dialog open onClose={vi.fn()} label="ヘルプ">
        <p>本文</p>
      </Dialog>,
    );
    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveAttribute("aria-modal", "true");
    expect(dialog).toHaveAccessibleName("ヘルプ");
  });

  it("closes on Escape", () => {
    const onClose = vi.fn();
    render(
      <Dialog open onClose={onClose} label="ヘルプ">
        <p>本文</p>
      </Dialog>,
    );
    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
    expect(onClose).toHaveBeenCalled();
  });

  it("closes when the scrim is clicked (dismissable, the default)", () => {
    const onClose = vi.fn();
    render(
      <Dialog open onClose={onClose} scrimLabel="閉じる">
        <p>本文</p>
      </Dialog>,
    );
    fireEvent.click(screen.getByLabelText("閉じる"));
    expect(onClose).toHaveBeenCalled();
  });

  it("omits the click-to-close scrim when not dismissable (explicit-action flows)", () => {
    const onClose = vi.fn();
    render(
      <Dialog open onClose={onClose} dismissable={false} scrimLabel="閉じる">
        <p>本文</p>
      </Dialog>,
    );
    expect(screen.queryByLabelText("閉じる")).not.toBeInTheDocument();
  });

  it("renders a right-drawer scrim with the given label", () => {
    const onClose = vi.fn();
    render(
      <Dialog
        open
        onClose={onClose}
        placement="drawer-right"
        scrimLabel="ヘルプを閉じる"
        testId="drawer"
      >
        <p>本文</p>
      </Dialog>,
    );
    fireEvent.click(screen.getByLabelText("ヘルプを閉じる"));
    expect(onClose).toHaveBeenCalled();
  });
});
