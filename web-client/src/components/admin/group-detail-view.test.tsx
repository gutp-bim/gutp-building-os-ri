import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { AdminGroupDetail } from "@/lib/admin/types";
import { GroupDetailView } from "./group-detail-view";

describe("GroupDetailView", () => {
  it("renders the group attributes", () => {
    const group: AdminGroupDetail = {
      id: "g1",
      name: "Floor 1 Ops",
      description: "1階運用",
      createdAt: "2024-03-09T00:00:00Z",
    };
    render(<GroupDetailView group={group} />);
    const detail = screen.getByTestId("group-detail");
    expect(detail).toHaveTextContent("Floor 1 Ops");
    expect(detail).toHaveTextContent("g1");
    expect(detail).toHaveTextContent("1階運用");
  });

  it("falls back to a dash for missing attributes", () => {
    render(<GroupDetailView group={{ id: "g2", name: "Bare" }} />);
    expect(screen.getByTestId("group-detail")).toHaveTextContent("—");
  });
});
