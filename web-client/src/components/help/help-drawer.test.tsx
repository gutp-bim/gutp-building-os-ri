import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { GlossaryTerm, HelpEntry } from "@/lib/help/types";
import { HelpDrawer } from "./help-drawer";

const entry: HelpEntry = {
  key: "screen.a",
  title: "稼働状態",
  body: ["一段落目", "二段落目"],
  relatedTerms: ["メッセージレート"],
};
const terms: GlossaryTerm[] = [
  { term: "メッセージレート", definition: "毎秒の処理件数", category: "metric" },
];

describe("HelpDrawer", () => {
  it("renders nothing when closed", () => {
    const { container } = render(
      <HelpDrawer entry={entry} terms={terms} open={false} onClose={vi.fn()} />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("renders the title, body paragraphs and related terms when open", () => {
    render(<HelpDrawer entry={entry} terms={terms} open onClose={vi.fn()} />);
    expect(screen.getByText("稼働状態")).toBeInTheDocument();
    expect(screen.getByText("一段落目")).toBeInTheDocument();
    expect(screen.getByText("二段落目")).toBeInTheDocument();
    const related = screen.getByTestId("help-related-terms");
    expect(related).toHaveTextContent("メッセージレート");
    expect(related).toHaveTextContent("毎秒の処理件数");
  });

  it("calls onClose from the close button", () => {
    const onClose = vi.fn();
    render(<HelpDrawer entry={entry} terms={terms} open onClose={onClose} />);
    fireEvent.click(screen.getByLabelText("閉じる"));
    expect(onClose).toHaveBeenCalled();
  });

  it("shows a fallback when no entry is bound", () => {
    render(<HelpDrawer entry={null} terms={[]} open onClose={vi.fn()} />);
    expect(screen.getByTestId("help-missing")).toBeInTheDocument();
  });
});
