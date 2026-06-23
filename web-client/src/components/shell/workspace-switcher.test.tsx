import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { workspacesForRole } from "@/lib/auth/workspaces";
import { WorkspaceSwitcher } from "./workspace-switcher";

describe("WorkspaceSwitcher", () => {
  it("renders nothing when the role grants no workspaces", () => {
    const { container } = render(
      <WorkspaceSwitcher workspaces={workspacesForRole(null)} current={null} onSelect={vi.fn()} />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("shows a static label (no switcher) for a single-workspace role", () => {
    render(
      <WorkspaceSwitcher
        workspaces={workspacesForRole("operator")}
        current="operator"
        onSelect={vi.fn()}
      />,
    );
    expect(screen.getByTestId("workspace-label")).toHaveTextContent("運用（建物）");
    expect(screen.queryByRole("button", { name: "ワークスペースを切り替え" })).toBeNull();
  });

  it("shows a switch control for an admin (multiple workspaces) with the current label", () => {
    render(
      <WorkspaceSwitcher
        workspaces={workspacesForRole("admin")}
        current="admin"
        onSelect={vi.fn()}
      />,
    );
    const trigger = screen.getByRole("button", { name: "ワークスペースを切り替え" });
    expect(trigger).toHaveTextContent("管理");
    expect(screen.queryByTestId("workspace-label")).toBeNull();
  });
});
