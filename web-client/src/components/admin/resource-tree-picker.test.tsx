import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { TreeLoaders } from "./resource-tree-picker";
import { ResourceTreePicker } from "./resource-tree-picker";

function makeLoaders(): TreeLoaders {
  return {
    loadRoots: vi.fn().mockResolvedValue([{ type: "building", id: "b1", name: "本館" }]),
    loadChildren: vi
      .fn()
      .mockResolvedValue([{ type: "floor", id: "f1", name: "1F" }]),
  };
}

describe("ResourceTreePicker", () => {
  it("renders root buildings from the loader", async () => {
    render(<ResourceTreePicker loaders={makeLoaders()} onSelect={vi.fn()} onClose={vi.fn()} />);
    expect(await screen.findByText("本館")).toBeInTheDocument();
  });

  it("lazily loads and shows children on expand", async () => {
    const loaders = makeLoaders();
    render(<ResourceTreePicker loaders={loaders} onSelect={vi.fn()} onClose={vi.fn()} />);
    await screen.findByText("本館");
    fireEvent.click(screen.getByRole("button", { name: "本館 を展開" }));
    expect(await screen.findByText("1F")).toBeInTheDocument();
    expect(loaders.loadChildren).toHaveBeenCalledWith("building", "b1");
  });

  it("calls onSelect with the node identity when 選択 is clicked", async () => {
    const onSelect = vi.fn();
    render(<ResourceTreePicker loaders={makeLoaders()} onSelect={onSelect} onClose={vi.fn()} />);
    await screen.findByText("本館");
    fireEvent.click(screen.getByRole("button", { name: "選択" }));
    expect(onSelect).toHaveBeenCalledWith("building", "b1", "本館");
  });

  it("shows an empty state when there are no buildings", async () => {
    const loaders: TreeLoaders = { loadRoots: vi.fn().mockResolvedValue([]), loadChildren: vi.fn() };
    render(<ResourceTreePicker loaders={loaders} onSelect={vi.fn()} onClose={vi.fn()} />);
    expect(await screen.findByTestId("tree-empty")).toBeInTheDocument();
  });

  it("surfaces a load error", async () => {
    const loaders: TreeLoaders = {
      loadRoots: vi.fn().mockRejectedValue(new Error("boom")),
      loadChildren: vi.fn(),
    };
    render(<ResourceTreePicker loaders={loaders} onSelect={vi.fn()} onClose={vi.fn()} />);
    await waitFor(() => expect(screen.getByTestId("tree-error")).toBeInTheDocument());
  });
});
