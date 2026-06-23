import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Breadcrumb } from "./breadcrumb";

describe("Breadcrumb", () => {
  it("renders the workspace as a link and the current page as aria-current (no link)", () => {
    render(<Breadcrumb pathname="/admin/users" />);
    const nav = screen.getByTestId("breadcrumb");
    expect(nav).toHaveTextContent("管理");
    expect(nav).toHaveTextContent("ユーザー");

    const workspaceLink = screen.getByRole("link", { name: "管理" });
    expect(workspaceLink).toHaveAttribute("href", "/admin/users");

    const current = screen.getByText("ユーザー");
    expect(current).toHaveAttribute("aria-current", "page");
    // current page is not a link
    expect(screen.queryByRole("link", { name: "ユーザー" })).toBeNull();
  });

  it("renders nothing for an unmatched path", () => {
    const { container } = render(<Breadcrumb pathname="/" />);
    expect(container).toBeEmptyDOMElement();
  });

  it("shows workspace + current page on a detail deep-link page", () => {
    render(<Breadcrumb pathname="/buildings" />);
    // workspace links to the operator landing page (/resources); page ("建物") is the current crumb
    expect(screen.getByRole("link", { name: "運用（建物）" })).toHaveAttribute(
      "href",
      "/resources",
    );
    expect(screen.getByText("建物")).toHaveAttribute("aria-current", "page");
  });
});
