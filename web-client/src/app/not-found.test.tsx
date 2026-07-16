import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import NotFound from "./not-found";

describe("global NotFound (#190)", () => {
  it("shows a Japanese message with a route back to /home", () => {
    render(<NotFound />);
    expect(screen.getByText("ページが見つかりません")).toBeInTheDocument();
    const home = screen.getByRole("link", { name: /ホーム/ });
    expect(home).toHaveAttribute("href", "/home");
  });
});
