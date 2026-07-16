import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import NotFound from "./not-found";
import ErrorBoundary from "./error";

describe("building detail boundaries — current IA links (#190)", () => {
  it("not-found links to /resources and /home, not the retired /buildings list", () => {
    render(<NotFound />);
    expect(screen.getByRole("link", { name: /リソース/ })).toHaveAttribute(
      "href",
      "/resources",
    );
    expect(screen.getByRole("link", { name: /ホーム/ })).toHaveAttribute(
      "href",
      "/home",
    );
    expect(screen.queryByRole("link", { name: "建物一覧に戻る" })).toBeNull();
  });

  it("error boundary offers retry plus /resources and /home links", () => {
    render(<ErrorBoundary error={new Error("boom")} reset={vi.fn()} />);
    expect(
      screen.getByRole("button", { name: "もう一度試す" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /リソース/ })).toHaveAttribute(
      "href",
      "/resources",
    );
    expect(screen.getByRole("link", { name: /ホーム/ })).toHaveAttribute(
      "href",
      "/home",
    );
  });
});
