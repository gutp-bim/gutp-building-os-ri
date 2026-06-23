import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { AdminGroup } from "@/lib/admin/types";
import { GroupsTable } from "./groups-table";

describe("GroupsTable", () => {
  it("renders a row per group with a link to the detail page", () => {
    const groups: AdminGroup[] = [
      { id: "g1", name: "Floor 1 Ops", description: "1階運用", createdAt: "2024-03-09T00:00:00Z" },
    ];
    render(<GroupsTable groups={groups} />);
    const link = screen.getByRole("link", { name: "Floor 1 Ops" });
    expect(link).toHaveAttribute("href", "/admin/groups/g1");
    const row = screen.getByTestId("group-row-g1");
    expect(row).toHaveTextContent("g1");
    expect(row).toHaveTextContent("1階運用");
  });

  it("shows an empty state when there are no groups", () => {
    render(<GroupsTable groups={[]} />);
    expect(screen.getByTestId("groups-empty")).toBeInTheDocument();
  });

  it("falls back gracefully for missing fields", () => {
    render(<GroupsTable groups={[{ id: "g2", name: "Bare" }]} />);
    const row = screen.getByTestId("group-row-g2");
    expect(row).toHaveTextContent("—"); // description + created date dashes
  });
});
