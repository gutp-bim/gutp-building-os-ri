import type { SearchHit } from "@/lib/resources/types";
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
