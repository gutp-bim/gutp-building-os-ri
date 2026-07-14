import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { GlossaryTooltip } from "./glossary-tooltip";

describe("GlossaryTooltip", () => {
  it("decorates a known term with its definition as a tooltip", () => {
    render(<GlossaryTooltip term="ポイント" />);
    const el = screen.getByTestId("glossary-ポイント");
    expect(el).toHaveAttribute("title");
    expect(el.getAttribute("title")).toContain("計測点");
  });

  it("decorates a newly-added core concept term end-to-end (#160)", () => {
    render(<GlossaryTooltip term="デジタルツイン" />);
    const el = screen.getByTestId("glossary-デジタルツイン");
    expect(el.getAttribute("title")).toContain("source of truth");
  });

  it("renders children plainly for an unknown term", () => {
    render(<GlossaryTooltip term="unknown-term">ラベル</GlossaryTooltip>);
    expect(screen.getByText("ラベル")).toBeInTheDocument();
    expect(screen.queryByTestId("glossary-unknown-term")).not.toBeInTheDocument();
  });
});
