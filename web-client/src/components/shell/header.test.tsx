import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

// Isolate the header's own logic from the workspace switcher / user menu / tour button.
vi.mock("./workspace-switcher", () => ({ WorkspaceSwitcher: () => <div /> }));
vi.mock("./user-menu", () => ({ UserMenu: () => <div /> }));
vi.mock("@/components/onboarding/replay-tour-button", () => ({ ReplayTourButton: () => <div /> }));
vi.mock("next/link", () => ({
  default: ({ children, ...rest }: { children: React.ReactNode }) => <a {...rest}>{children}</a>,
}));

import { Header } from "./header";

const base = {
  workspaces: [],
  currentWorkspace: null,
  onSelectWorkspace: vi.fn(),
  displayName: "u",
  onSignOut: vi.fn(),
};

describe("Header sidebar toggle (#199)", () => {
  it("shows a hamburger that toggles the sidebar", () => {
    const onToggleSidebar = vi.fn();
    render(<Header {...base} onToggleSidebar={onToggleSidebar} sidebarOpen={false} />);
    const toggle = screen.getByTestId("sidebar-toggle");
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    expect(toggle).toHaveAttribute("aria-controls", "app-sidebar");
    fireEvent.click(toggle);
    expect(onToggleSidebar).toHaveBeenCalledOnce();
  });

  it("reflects the open state via aria-expanded", () => {
    render(<Header {...base} onToggleSidebar={vi.fn()} sidebarOpen={true} />);
    expect(screen.getByTestId("sidebar-toggle")).toHaveAttribute("aria-expanded", "true");
  });

  it("omits the hamburger when no toggle handler is given", () => {
    render(<Header {...base} />);
    expect(screen.queryByTestId("sidebar-toggle")).not.toBeInTheDocument();
  });
});
