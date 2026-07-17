import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ChatMessage } from "@/lib/assistant/types";
import { AssistantPanel } from "./assistant-panel";

describe("AssistantPanel", () => {
  it("renders the transcript", () => {
    const messages: ChatMessage[] = [
      { role: "user", content: "使い方は？" },
      { role: "assistant", content: "こう使います" },
    ];
    render(<AssistantPanel messages={messages} onSend={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByText("使い方は？")).toBeInTheDocument();
    expect(screen.getByText("こう使います")).toBeInTheDocument();
  });

  it("sends the trimmed draft and clears the input", () => {
    const onSend = vi.fn();
    render(<AssistantPanel messages={[]} onSend={onSend} onClose={vi.fn()} />);
    const input = screen.getByLabelText("質問を入力");
    fireEvent.change(input, { target: { value: "  ヘルプ  " } });
    fireEvent.click(screen.getByRole("button", { name: "送信" }));
    expect(onSend).toHaveBeenCalledWith("ヘルプ");
    expect(input).toHaveValue("");
  });

  it("does not send while busy or when empty", () => {
    const onSend = vi.fn();
    render(<AssistantPanel messages={[]} busy onSend={onSend} onClose={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: "送信" }));
    expect(onSend).not.toHaveBeenCalled();
  });

  it("shows an error", () => {
    render(<AssistantPanel messages={[]} error="無効です" onSend={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByTestId("assistant-error")).toHaveTextContent("無効です");
  });

  it("is a non-modal labelled dialog that closes on Escape (#198 review)", () => {
    const onClose = vi.fn();
    render(<AssistantPanel messages={[]} onSend={vi.fn()} onClose={onClose} />);
    const dialog = screen.getByRole("dialog");
    // A floating helper must not claim to be modal (it doesn't cover the app / trap focus).
    expect(dialog).not.toHaveAttribute("aria-modal");
    expect(dialog).toHaveAccessibleName("ヘルプアシスタント（実験的）");
    fireEvent.keyDown(dialog, { key: "Escape" });
    expect(onClose).toHaveBeenCalled();
  });
});
