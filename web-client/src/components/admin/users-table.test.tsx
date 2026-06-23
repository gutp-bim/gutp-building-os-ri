import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { AdminUser } from "@/lib/admin/types";
import { UsersTable } from "./users-table";

describe("UsersTable", () => {
  it("renders a row per user with a link to the detail page", () => {
    const users: AdminUser[] = [
      { id: "u1", displayName: "Alice", email: "a@x.jp", role: "admin", permissions: ["d:h:r,w"] },
    ];
    render(<UsersTable users={users} />);
    const link = screen.getByRole("link", { name: "Alice" });
    expect(link).toHaveAttribute("href", "/admin/users/u1");
    expect(screen.getByTestId("user-row-u1")).toHaveTextContent("a@x.jp");
    expect(screen.getByTestId("user-row-u1")).toHaveTextContent("管理者"); // role label
    expect(screen.getByTestId("user-status-u1")).toHaveTextContent("有効"); // enabled by default
    expect(screen.getByTestId("user-row-u1")).toHaveTextContent("1"); // permission count
  });

  it("shows an empty state when there are no users", () => {
    render(<UsersTable users={[]} />);
    expect(screen.getByTestId("users-empty")).toBeInTheDocument();
  });

  it("falls back gracefully for missing fields", () => {
    render(<UsersTable users={[{ id: "u2" }]} />);
    const row = screen.getByTestId("user-row-u2");
    expect(row).toHaveTextContent("—"); // email/role dashes
    expect(row).toHaveTextContent("0"); // no permissions
  });
});
