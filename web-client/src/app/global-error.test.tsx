import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import GlobalError from "./global-error";

describe("global-error boundary (#190)", () => {
  it("renders a Japanese fallback with a /home recovery link", () => {
    render(<GlobalError error={new Error("boom")} reset={vi.fn()} />);
    expect(screen.getByText("エラーが発生しました")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /ホーム/ })).toHaveAttribute(
      "href",
      "/home",
    );
  });
});
