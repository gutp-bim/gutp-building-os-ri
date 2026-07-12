import type { ResourceTreeLoaders } from "@/lib/resources/tree-loaders";
import type { ResourceRef } from "@/lib/resources/types";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ResourceTreeView } from "./resource-tree-view";

const building: ResourceRef = {
  type: "building",
  dtId: "urn:b1",
  id: "B1",
  name: "本館",
};
const building2: ResourceRef = {
  type: "building",
  dtId: "urn:b2",
  id: "B2",
  name: "別館",
};
const floor: ResourceRef = {
  type: "floor",
  dtId: "urn:f1",
  id: "F1",
  name: "1F",
};

function makeLoaders(): ResourceTreeLoaders {
  return {
    loadRoots: vi.fn().mockResolvedValue([building, building2]),
    loadChildren: vi.fn().mockResolvedValue([floor]),
  };
}

function makeSingleRootLoaders(): ResourceTreeLoaders {
  return {
    loadRoots: vi.fn().mockResolvedValue([building]),
    loadChildren: vi.fn().mockResolvedValue([floor]),
  };
}

describe("ResourceTreeView", () => {
  it("renders root buildings from the loader", async () => {
    render(<ResourceTreeView loaders={makeLoaders()} onSelect={vi.fn()} />);
    expect(await screen.findByText("本館")).toBeInTheDocument();
  });

  it("lazily loads and shows children on expand", async () => {
    const loaders = makeLoaders();
    render(<ResourceTreeView loaders={loaders} onSelect={vi.fn()} />);
    await screen.findByText("別館");
    // 本館 (index 0) auto-expands by default (#135); 別館 (index 1) still needs a manual click.
    fireEvent.click(screen.getByRole("button", { name: "別館 を展開" }));
    expect(await screen.findByText("1F")).toBeInTheDocument();
    expect(loaders.loadChildren).toHaveBeenCalledWith(building2);
  });

  it("auto-expands only the first root by default", async () => {
    const loaders = makeLoaders();
    render(<ResourceTreeView loaders={loaders} onSelect={vi.fn()} />);
    // 本館's children appear without a manual expand click...
    expect(await screen.findByText("1F")).toBeInTheDocument();
    // ...but 別館 stays collapsed until clicked.
    expect(
      screen.getByRole("button", { name: "別館 を展開" }),
    ).toBeInTheDocument();
    expect(loaders.loadChildren).toHaveBeenCalledWith(building);
    expect(loaders.loadChildren).not.toHaveBeenCalledWith(building2);
  });

  it("auto-selects the sole root building when nothing is selected yet (#135)", async () => {
    const onSelect = vi.fn();
    render(
      <ResourceTreeView loaders={makeSingleRootLoaders()} onSelect={onSelect} />,
    );
    await waitFor(() => expect(onSelect).toHaveBeenCalledWith(building));
  });

  it("does not auto-select the sole root building when a selection already exists", async () => {
    const onSelect = vi.fn();
    render(
      <ResourceTreeView
        loaders={makeSingleRootLoaders()}
        onSelect={onSelect}
        selectedKey="building:urn:b1"
      />,
    );
    await screen.findByText("本館");
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("does not auto-select when more than one root exists", async () => {
    const onSelect = vi.fn();
    render(<ResourceTreeView loaders={makeLoaders()} onSelect={onSelect} />);
    await screen.findByText("本館");
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("calls onSelect with the node ref when a row is clicked", async () => {
    const onSelect = vi.fn();
    render(<ResourceTreeView loaders={makeLoaders()} onSelect={onSelect} />);
    fireEvent.click(await screen.findByText("本館"));
    expect(onSelect).toHaveBeenCalledWith(building);
  });

  it("marks the selected node with aria-current", async () => {
    render(
      <ResourceTreeView
        loaders={makeLoaders()}
        onSelect={vi.fn()}
        selectedKey="building:urn:b1"
      />,
    );
    const row = await screen.findByText("本館");
    expect(row.closest("[aria-current]")).toHaveAttribute(
      "aria-current",
      "true",
    );
  });

  it("auto-expands the building given by autoExpandBuildingDtId", async () => {
    const loaders = makeLoaders();
    render(
      <ResourceTreeView
        loaders={loaders}
        onSelect={vi.fn()}
        autoExpandBuildingDtId="urn:b1"
      />,
    );
    // children appear without a manual expand click
    expect(await screen.findByText("1F")).toBeInTheDocument();
    await waitFor(() =>
      expect(loaders.loadChildren).toHaveBeenCalledWith(building),
    );
  });
});
