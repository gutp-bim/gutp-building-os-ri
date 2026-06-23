import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { AdminGroupResourceItem } from "@/lib/admin/types";
import { GroupResourceManager } from "./group-resource-manager";

const items: AdminGroupResourceItem[] = [
  { id: "r1", resourceType: "building", resourceId: "bldg-1" },
];

describe("GroupResourceManager", () => {
  it("renders existing items with remove buttons", () => {
    const onRemove = vi.fn();
    render(<GroupResourceManager items={items} onAdd={vi.fn()} onRemove={onRemove} />);
    const row = screen.getByTestId("resource-r1");
    expect(row).toHaveTextContent("building");
    expect(row).toHaveTextContent("bldg-1");
    fireEvent.click(screen.getByRole("button", { name: "リソース building:bldg-1 を削除" }));
    expect(onRemove).toHaveBeenCalledWith("r1");
  });

  it("shows the empty state with no items", () => {
    render(<GroupResourceManager items={[]} onAdd={vi.fn()} onRemove={vi.fn()} />);
    expect(screen.getByTestId("resources-empty")).toBeInTheDocument();
  });

  it("validates the add form before calling onAdd", () => {
    const onAdd = vi.fn();
    render(<GroupResourceManager items={[]} onAdd={onAdd} onRemove={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: "追加" }));
    expect(onAdd).not.toHaveBeenCalled();
    expect(screen.getByTestId("resource-add-error")).toBeInTheDocument();
  });

  it("passes the selected type and trimmed id to onAdd", () => {
    const onAdd = vi.fn();
    render(<GroupResourceManager items={[]} onAdd={onAdd} onRemove={vi.fn()} />);
    fireEvent.change(screen.getByLabelText("リソース種別"), { target: { value: "floor" } });
    fireEvent.change(screen.getByLabelText("リソース ID"), { target: { value: " flr-2 " } });
    fireEvent.click(screen.getByRole("button", { name: "追加" }));
    expect(onAdd).toHaveBeenCalledWith("floor", "flr-2");
  });
});
