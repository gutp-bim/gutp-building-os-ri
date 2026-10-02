import { render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

// The page pulls in the router, the auth cookie, the tree loaders, and four heavy child components.
// Stub them all so we can assert the responsive two-pane layout in isolation (#199 UX-10).
const replace = vi.fn();
let currentSearch = "";
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace }),
  useSearchParams: () => new URLSearchParams(currentSearch),
}));
vi.mock("js-cookie", () => ({ default: { get: () => undefined } }));
vi.mock("@/lib/resources/tree-loaders", () => ({ defaultTreeLoaders: {} }));
const resolveRef = vi.fn().mockResolvedValue(null);
vi.mock("@/lib/resources/repository", () => ({
  resolveRef: (...a: unknown[]) => resolveRef(...a),
  fetchResourceMetadata: () => Promise.resolve(undefined),
  updateResourceMetadata: vi.fn(),
}));
let boxProps: {
  initialFilters: unknown;
  onFiltersChange: (f: unknown) => void;
  onPick: (hit: unknown) => void;
} | null = null;
vi.mock("@/components/resources/resource-search-box", () => ({
  ResourceSearchBox: (props: NonNullable<typeof boxProps>) => {
    boxProps = props;
    return <div data-testid="search-box" />;
  },
}));
vi.mock("@/components/resources/resource-tree-view", () => ({
  ResourceTreeView: () => <div data-testid="tree-view" />,
}));
vi.mock("@/components/resources/resource-detail", () => ({
  ResourceDetail: () => <div data-testid="detail" />,
}));
vi.mock("@/components/resources/metadata-editor", () => ({
  MetadataEditor: () => <div data-testid="metadata-editor" />,
}));

import ResourcesPageComponent from "./page-component";

describe("ResourcesPageComponent layout (#199)", () => {
  it("stacks the two panes until lg and goes side-by-side only from lg up", () => {
    render(<ResourcesPageComponent />);
    const pane = screen.getByTestId("resource-two-pane");
    // Row only from `lg`, NOT `md`: the shell sidebar also turns static at `md`, so a two-pane at
    // 768px crushes the detail pane (#208 review). Guard against a regression back to md:flex-row.
    expect(pane).toHaveClass("flex", "flex-col", "lg:flex-row");
    expect(pane).not.toHaveClass("md:flex-row");
  });
});

describe("ResourcesPageComponent search filters in the URL (#454)", () => {
  it("starts the search box from the filters in the URL", () => {
    currentSearch = "q=vav&unit=degC&deviceType=AHU&deviceType=VAV&sel=point%3AP1";
    render(<ResourcesPageComponent />);
    expect(boxProps?.initialFilters).toMatchObject({
      q: "vav",
      units: ["degC"],
      deviceTypes: ["AHU", "VAV"],
    });
  });

  it("writes filter changes to the URL, keeping the selection", () => {
    currentSearch = "";
    window.history.replaceState({}, "", "/resources?sel=point%3AP1&q=old");
    render(<ResourcesPageComponent />);

    boxProps?.onFiltersChange({
      q: "new", type: "", tags: [], deviceTypes: ["AHU"], pointTypes: [], units: [], gatewayIds: [], freshness: ["stale"], alarm: [],
    });

    const url = replace.mock.calls.at(-1)?.[0] as string;
    expect(url).toContain("sel=point%3AP1");
    expect(url).toContain("q=new");
    expect(url).toContain("deviceType=AHU");
    expect(url).toContain("freshness=stale");
    expect(url).not.toContain("q=old");
  });
});

describe("ResourcesPageComponent picking a health-routed hit (#454)", () => {
  it("resolves the real node instead of using the point id as the twin id", async () => {
    currentSearch = "";
    replace.mockClear();
    resolveRef.mockResolvedValue({ type: "point", dtId: "urn:twin:P9", id: "P9", name: "Stale one" });
    render(<ResourcesPageComponent />);

    boxProps?.onPick({ type: "point", dtId: "", id: "P9", name: "Stale one", buildingDtId: null });

    await waitFor(() => expect(resolveRef).toHaveBeenCalledWith("point", "P9"));
    await waitFor(() => expect(replace).toHaveBeenCalled());
    expect(replace.mock.calls.at(-1)?.[0]).toContain("sel=point%3AP9");
  });

  it("selects directly when the hit already has its twin id", () => {
    currentSearch = "";
    resolveRef.mockClear();
    replace.mockClear();
    render(<ResourcesPageComponent />);

    boxProps?.onPick({ type: "device", dtId: "urn:d1", id: "D1", name: "AHU", buildingDtId: null });

    expect(resolveRef).not.toHaveBeenCalled();
    expect(replace.mock.calls.at(-1)?.[0]).toContain("sel=device%3Aurn%3Ad1");
  });

  it("a slow lookup does not overwrite a newer selection", async () => {
    currentSearch = "";
    replace.mockClear();
    let finishSlow: (r: unknown) => void = () => {};
    resolveRef.mockReturnValueOnce(new Promise((res) => (finishSlow = res)));
    render(<ResourcesPageComponent />);

    boxProps?.onPick({ type: "point", dtId: "", id: "SLOW", name: "slow", buildingDtId: null });
    boxProps?.onPick({ type: "device", dtId: "urn:d1", id: "D1", name: "AHU", buildingDtId: null });
    finishSlow({ type: "point", dtId: "urn:slow", id: "SLOW", name: "slow" });
    await Promise.resolve();
    await Promise.resolve();

    const urls = replace.mock.calls.map((c) => c[0] as string);
    expect(urls.some((u) => u.includes("SLOW"))).toBe(false);
    expect(urls.at(-1)).toContain("sel=device");
  });

  it("an external change of sel (Back/Forward) supersedes a lookup in flight", async () => {
    currentSearch = "";
    replace.mockClear();
    let finishSlow: (r: unknown) => void = () => {};
    resolveRef
      .mockReturnValueOnce(new Promise((res) => (finishSlow = res)))
      .mockResolvedValue(null);
    const { rerender } = render(<ResourcesPageComponent />);

    boxProps?.onPick({ type: "point", dtId: "", id: "SLOW", name: "slow", buildingDtId: null });
    currentSearch = "sel=device%3Aurn%3Ad1"; // the URL moved on without us
    rerender(<ResourcesPageComponent />);
    finishSlow({ type: "point", dtId: "urn:slow", id: "SLOW", name: "slow" });
    await Promise.resolve();
    await Promise.resolve();

    expect(replace.mock.calls.map((c) => c[0] as string).some((u) => u.includes("SLOW"))).toBe(false);
  });

  it("a selection keeps the filters currently in the URL, not the ones at click time", async () => {
    currentSearch = "";
    replace.mockClear();
    window.history.replaceState({}, "", "/resources?q=newer&unit=ppm");
    render(<ResourcesPageComponent />);

    boxProps?.onPick({ type: "device", dtId: "urn:d1", id: "D1", name: "AHU", buildingDtId: null });

    const url = replace.mock.calls.at(-1)?.[0] as string;
    expect(url).toContain("q=newer");
    expect(url).toContain("unit=ppm");
    expect(url).toContain("sel=device");
  });
});
