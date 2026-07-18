import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

// The page pulls in the router, the auth cookie, the tree loaders, and four heavy child components.
// Stub them all so we can assert the responsive two-pane layout in isolation (#199 UX-10).
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn() }),
  useSearchParams: () => new URLSearchParams(),
}));
vi.mock("js-cookie", () => ({ default: { get: () => undefined } }));
vi.mock("@/lib/resources/tree-loaders", () => ({ defaultTreeLoaders: {} }));
vi.mock("@/components/resources/resource-search-box", () => ({
  ResourceSearchBox: () => <div data-testid="search-box" />,
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
