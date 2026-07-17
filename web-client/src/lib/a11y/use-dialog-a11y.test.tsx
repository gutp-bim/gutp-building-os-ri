import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useRef, useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { useDialogA11y } from "./use-dialog-a11y";

/** A dialog opened by a trigger button, so focus restoration has a real element to return to. */
function Harness({ onClose = () => {}, modal }: { onClose?: () => void; modal?: boolean }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useDialogA11y(ref, {
    open,
    modal,
    onClose: () => {
      onClose();
      setOpen(false);
    },
  });
  return (
    <div>
      <button data-testid="trigger" onClick={() => setOpen(true)}>
        open
      </button>
      <button data-testid="outside">outside</button>
      {open && (
        <div ref={ref} role="dialog" aria-modal="true" tabIndex={-1} data-testid="dialog">
          <button data-testid="first">first</button>
          <button data-testid="last" onClick={() => setOpen(false)}>
            last
          </button>
        </div>
      )}
    </div>
  );
}

describe("useDialogA11y", () => {
  it("moves focus to the first focusable element when opened", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByTestId("trigger"));
    expect(screen.getByTestId("first")).toHaveFocus();
  });

  it("calls onClose on Escape", async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<Harness onClose={onClose} />);
    await user.click(screen.getByTestId("trigger"));
    await user.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("restores focus to the trigger when closed", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByTestId("trigger"));
    // Close from inside the dialog; focus must return to the opener, not fall to <body>.
    await user.click(screen.getByTestId("last"));
    expect(screen.getByTestId("trigger")).toHaveFocus();
  });

  it("traps Tab within the dialog (last wraps to first)", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByTestId("trigger"));
    screen.getByTestId("last").focus();
    await user.tab();
    expect(screen.getByTestId("first")).toHaveFocus();
  });

  it("traps Shift+Tab within the dialog (first wraps to last)", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByTestId("trigger"));
    screen.getByTestId("first").focus();
    await user.tab({ shift: true });
    expect(screen.getByTestId("last")).toHaveFocus();
  });

  it("pulls focus back into a modal dialog when it escapes to the background (#198 review)", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByTestId("trigger"));
    // Simulate focus escaping to a background element (e.g. a pointer click), which the keydown
    // trap alone cannot catch. The document-level focusin guard must return focus to the dialog.
    screen.getByTestId("outside").focus();
    expect(screen.getByTestId("first")).toHaveFocus();
  });

  it("does not trap focus when non-modal (modal: false)", async () => {
    const user = userEvent.setup();
    render(<Harness modal={false} />);
    await user.click(screen.getByTestId("trigger"));
    // A non-modal panel must let focus leave freely — no focusin guard pulls it back.
    screen.getByTestId("outside").focus();
    expect(screen.getByTestId("outside")).toHaveFocus();
  });
});
