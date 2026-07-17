import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

// Render next/link as a plain anchor so onClick fires without an app-router context in the test.
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

function setup(overrides: Partial<Parameters<typeof Sidebar>[0]> = {}) {
  const onClose = vi.fn();
  render(
    <Sidebar
      workspace="operator"
      permissions={[]}
      pathname="/home"
      open
      onClose={onClose}
      {...overrides}
    />,
  );
  return { onClose };
}

describe("Sidebar drawer (#199)", () => {
  it("renders the workspace nav items", () => {
    setup();
    expect(screen.getByRole("link", { name: "ホーム" })).toBeInTheDocument();
  });

  it("shows a scrim while open and closes on scrim click", () => {
    const { onClose } = setup();
    fireEvent.click(screen.getByTestId("sidebar-scrim"));
    expect(onClose).toHaveBeenCalled();
  });

  it("renders no scrim when closed", () => {
    setup({ open: false });
    expect(screen.queryByTestId("sidebar-scrim")).not.toBeInTheDocument();
  });

  it("closes on Escape while open", () => {
    const { onClose } = setup();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalled();
  });

  it("closes when a nav link is tapped", () => {
    const { onClose } = setup();
    fireEvent.click(screen.getByRole("link", { name: "ホーム" }));
    expect(onClose).toHaveBeenCalled();
  });

  it("is off-canvas when closed and in view when open", () => {
    const { rerender } = render(
      <Sidebar workspace="operator" permissions={[]} pathname="/home" open={false} onClose={vi.fn()} />,
    );
    expect(screen.getByTestId("sidebar")).toHaveClass("-translate-x-full");
    rerender(
      <Sidebar workspace="operator" permissions={[]} pathname="/home" open onClose={vi.fn()} />,
    );
    expect(screen.getByTestId("sidebar")).toHaveClass("translate-x-0");
  });
});
