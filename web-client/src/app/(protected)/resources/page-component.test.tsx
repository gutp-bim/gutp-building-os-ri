import { render, screen } from "@testing-library/react";
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
let boxProps: {
  initialFilters: unknown;
  onFiltersChange: (f: unknown) => void;
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
