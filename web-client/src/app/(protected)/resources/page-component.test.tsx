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
  it("stacks the two panes vertically on narrow viewports and side-by-side from md up", () => {
    render(<ResourcesPageComponent />);
    const pane = screen.getByTestId("resource-two-pane");
    // Column by default (mobile), row from `md` — so the detail pane isn't crushed on a phone.
    expect(pane).toHaveClass("flex", "flex-col", "md:flex-row");
  });
});
