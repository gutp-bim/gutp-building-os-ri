import type { SearchHit } from "@/lib/resources/types";
import { EMPTY_FILTERS } from "@/lib/resources/search-filters";
import type { ResourceFacetsResult } from "@/lib/resources/types";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ResourceSearchBox } from "./resource-search-box";

const hit: SearchHit = { type: "point", dtId: "urn:pt1", id: "PT001", name: "室温", buildingDtId: null };

function lastCall(mock: ReturnType<typeof vi.fn>) {
  return mock.mock.calls.at(-1)?.[0];
}

describe("ResourceSearchBox tag filter (#332)", () => {
  it("adds a tag chip on Enter and searches with that tag (no q needed)", async () => {
    const search = vi.fn().mockResolvedValue([hit]);
    render(<ResourceSearchBox onPick={vi.fn()} search={search} />);

    fireEvent.change(screen.getByTestId("tag-input"), { target: { value: "hvac" } });
    fireEvent.keyDown(screen.getByTestId("tag-input"), { key: "Enter" });

    expect(screen.getByTestId("tag-chip-hvac")).toBeInTheDocument();
    await waitFor(() => expect(search).toHaveBeenCalled());
    expect(lastCall(search)).toMatchObject({ tags: ["hvac"] });
    // tag-only search → result rendered
    expect(await screen.findByText("室温")).toBeInTheDocument();
  });

  it("ANDs multiple tags and combines with q", async () => {
    const search = vi.fn().mockResolvedValue([hit]);
    render(<ResourceSearchBox onPick={vi.fn()} search={search} />);

    fireEvent.change(screen.getByLabelText("リソース検索"), { target: { value: "temp" } });
    const tagInput = screen.getByTestId("tag-input");
    fireEvent.change(tagInput, { target: { value: "hvac" } });
    fireEvent.keyDown(tagInput, { key: "Enter" });
    fireEvent.change(tagInput, { target: { value: "temperature" } });
    fireEvent.keyDown(tagInput, { key: "Enter" });

    await waitFor(() =>
      expect(lastCall(search)).toMatchObject({ q: "temp", tags: ["hvac", "temperature"] }),
    );
  });

  it("does not add blank or duplicate tags", async () => {
    const search = vi.fn().mockResolvedValue([]);
    render(<ResourceSearchBox onPick={vi.fn()} search={search} />);
    const tagInput = screen.getByTestId("tag-input");

    fireEvent.change(tagInput, { target: { value: "   " } });
    fireEvent.keyDown(tagInput, { key: "Enter" });
    expect(screen.queryByTestId("tag-chips")).not.toBeInTheDocument();

    fireEvent.change(tagInput, { target: { value: "hvac" } });
    fireEvent.keyDown(tagInput, { key: "Enter" });
    fireEvent.change(tagInput, { target: { value: "hvac" } });
    fireEvent.keyDown(tagInput, { key: "Enter" });

    expect(screen.getAllByTestId(/^tag-chip-/)).toHaveLength(1);
  });

  it("removing the only chip with no q clears results", async () => {
    const search = vi.fn().mockResolvedValue([hit]);
    render(<ResourceSearchBox onPick={vi.fn()} search={search} />);

    const tagInput = screen.getByTestId("tag-input");
    fireEvent.change(tagInput, { target: { value: "hvac" } });
    fireEvent.keyDown(tagInput, { key: "Enter" });
    await waitFor(() => expect(search).toHaveBeenCalled());

    fireEvent.click(screen.getByTestId("tag-chip-hvac"));
    expect(screen.queryByTestId("tag-chip-hvac")).not.toBeInTheDocument();
    // criteria empty → result list cleared
    await waitFor(() => expect(screen.queryByText("室温")).not.toBeInTheDocument());
  });
});

describe("ResourceSearchBox tag suggestions (#454)", () => {
  it("offers existing tags as completions for the tag input", async () => {
    const suggest = vi.fn().mockResolvedValue([{ tag: "temperature", count: 3 }]);
    render(<ResourceSearchBox onPick={vi.fn()} search={vi.fn()} suggestTags={suggest} />);

    fireEvent.change(screen.getByTestId("tag-input"), { target: { value: "tem" } });

    await waitFor(() => expect(suggest).toHaveBeenCalledWith("tem"));
    await waitFor(() =>
      expect(document.querySelector("datalist option")?.getAttribute("value")).toBe("temperature"),
    );
  });
});

describe("ResourceSearchBox facets (#454)", () => {
  const facets: ResourceFacetsResult = {
    total: 3,
    truncated: false,
    types: [{ value: "point", count: 3 }],
    deviceTypes: [{ value: "AHU", count: 2 }],
    pointTypes: [],
    units: [],
    gateways: [],
  };

  it("checking a facet re-searches with it and reports the filters", async () => {
    const search = vi.fn().mockResolvedValue([hit]);
    const loadFacets = vi.fn().mockResolvedValue(facets);
    const onFiltersChange = vi.fn();
    render(
      <ResourceSearchBox
        onPick={vi.fn()}
        search={search}
        loadFacets={loadFacets}
        onFiltersChange={onFiltersChange}
      />,
    );

    fireEvent.change(screen.getByLabelText("リソース検索"), { target: { value: "air" } });
    fireEvent.click(await screen.findByLabelText("AHU（2）"));

    await waitFor(() => expect(lastCall(search)).toMatchObject({ q: "air", deviceTypes: ["AHU"] }));
    await waitFor(() => expect(lastCall(loadFacets)).toMatchObject({ q: "air", deviceTypes: ["AHU"] }));
    expect(onFiltersChange).toHaveBeenLastCalledWith(expect.objectContaining({ deviceTypes: ["AHU"] }));
  });

  it("starts from the filters it is given (a URL) and searches immediately", async () => {
    const search = vi.fn().mockResolvedValue([hit]);
    render(
      <ResourceSearchBox
        onPick={vi.fn()}
        search={search}
        loadFacets={vi.fn().mockResolvedValue(facets)}
        initialFilters={{ ...EMPTY_FILTERS, units: ["degC"] }}
      />,
    );

    await waitFor(() => expect(lastCall(search)).toMatchObject({ units: ["degC"] }));
    expect(await screen.findByText("室温")).toBeInTheDocument();
  });

  it("a facet-only search (no text, no tag) is a search", async () => {
    const search = vi.fn().mockResolvedValue([hit]);
    render(
      <ResourceSearchBox
        onPick={vi.fn()}
        search={search}
        loadFacets={vi.fn().mockResolvedValue(facets)}
        initialFilters={{ ...EMPTY_FILTERS, deviceTypes: ["AHU"] }}
      />,
    );
    await waitFor(() => expect(search).toHaveBeenCalled());
  });

  it("the facet toggle opens the panel on an empty form without searching", async () => {
    const search = vi.fn();
    const loadFacets = vi.fn().mockResolvedValue(facets);
    render(<ResourceSearchBox onPick={vi.fn()} search={search} loadFacets={loadFacets} />);

    expect(screen.queryByTestId("facet-panel")).not.toBeInTheDocument();
    fireEvent.click(screen.getByTestId("facet-toggle"));

    expect(await screen.findByTestId("facet-panel")).toBeInTheDocument();
    expect(search).not.toHaveBeenCalled();
  });

  it("a failed facet lookup hides the panel but the search still works", async () => {
    const search = vi.fn().mockResolvedValue([hit]);
    render(
      <ResourceSearchBox
        onPick={vi.fn()}
        search={search}
        loadFacets={vi.fn().mockRejectedValue(new Error("boom"))}
      />,
    );
    fireEvent.change(screen.getByLabelText("リソース検索"), { target: { value: "air" } });

    expect(await screen.findByText("室温")).toBeInTheDocument();
    expect(screen.queryByTestId("facet-panel")).not.toBeInTheDocument();
  });
});
