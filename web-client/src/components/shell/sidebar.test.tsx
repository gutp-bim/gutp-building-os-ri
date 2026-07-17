import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

// Render next/link as a plain anchor so onClick fires (and it stays focusable) without an app-router
// context in the test.
vi.mock("next/link", () => ({
  default: ({
    href,
    onClick,
    children,
    ...rest
  }: {
    href: string;
    onClick?: () => void;
    children: React.ReactNode;
  }) => (
    <a href={href} onClick={onClick} {...rest}>
      {children}
    </a>
  ),
}));

import { Sidebar } from "./sidebar";

function renderSidebar(overrides: Partial<Parameters<typeof Sidebar>[0]> = {}) {
  const onClose = vi.fn();
  const utils = render(
    <Sidebar
      workspace="operator"
      permissions={[]}
      pathname="/home"
      open
      onClose={onClose}
      {...overrides}
    />,
  );
  return { onClose, ...utils };
}

describe("Sidebar (#199)", () => {
  it("renders the workspace nav items in the desktop column", () => {
    renderSidebar({ open: false });
    const desktop = screen.getByTestId("sidebar-desktop");
    expect(within(desktop).getByRole("link", { name: "ホーム" })).toBeInTheDocument();
  });

  // (a) closed → nothing behind the (absent) scrim is in the tab order.
  it("does not render the mobile drawer or scrim while closed", () => {
    renderSidebar({ open: false });
    expect(screen.queryByTestId("sidebar-drawer")).not.toBeInTheDocument();
    expect(screen.queryByTestId("sidebar-scrim")).not.toBeInTheDocument();
  });

  it("renders the mobile drawer as a modal dialog while open", () => {
    renderSidebar();
    const drawer = screen.getByTestId("sidebar-drawer");
    expect(drawer).toHaveAttribute("role", "dialog");
    expect(drawer).toHaveAttribute("aria-modal", "true");
    expect(drawer).toHaveAttribute("id", "app-sidebar");
    expect(screen.getByTestId("sidebar-scrim")).toBeInTheDocument();
  });

  // (b) open → focus moves into the drawer.
  it("moves focus into the drawer on open", () => {
    renderSidebar();
    const drawer = screen.getByTestId("sidebar-drawer");
    expect(drawer.contains(document.activeElement)).toBe(true);
  });

  // (c) Tab / Shift+Tab cycle within the drawer.
  it("traps Tab and Shift+Tab within the drawer", () => {
    renderSidebar();
    const drawer = screen.getByTestId("sidebar-drawer");
    const first = within(drawer).getByRole("button", { name: "閉じる" });
    const links = within(drawer).getAllByRole("link");
    const last = links[links.length - 1];

    last.focus();
    fireEvent.keyDown(drawer, { key: "Tab" });
    expect(document.activeElement).toBe(first);

    first.focus();
    fireEvent.keyDown(drawer, { key: "Tab", shiftKey: true });
    expect(document.activeElement).toBe(last);
  });

  it("closes on scrim click", () => {
    const { onClose } = renderSidebar();
    fireEvent.click(screen.getByTestId("sidebar-scrim"));
    expect(onClose).toHaveBeenCalled();
  });

  it("closes on Escape", () => {
    const { onClose } = renderSidebar();
    fireEvent.keyDown(screen.getByTestId("sidebar-drawer"), { key: "Escape" });
    expect(onClose).toHaveBeenCalled();
  });

  it("closes when a nav link is tapped", () => {
    const { onClose } = renderSidebar();
    const drawer = screen.getByTestId("sidebar-drawer");
    fireEvent.click(within(drawer).getByRole("link", { name: "ホーム" }));
    expect(onClose).toHaveBeenCalled();
  });

  // (d) closing returns focus to the trigger (the header hamburger stand-in).
  it("restores focus to the trigger after the drawer closes", () => {
    function Harness({ open }: { open: boolean }) {
      return (
        <>
          <button data-testid="trigger" type="button">
            メニュー
          </button>
          <Sidebar
            workspace="operator"
            permissions={[]}
            pathname="/home"
            open={open}
            onClose={() => {}}
          />
        </>
      );
    }

    const { rerender } = render(<Harness open={false} />);
    const trigger = screen.getByTestId("trigger");
    trigger.focus();
    expect(document.activeElement).toBe(trigger);

    rerender(<Harness open={true} />);
    expect(screen.getByTestId("sidebar-drawer").contains(document.activeElement)).toBe(true);

    rerender(<Harness open={false} />);
    expect(document.activeElement).toBe(trigger);
  });
});
