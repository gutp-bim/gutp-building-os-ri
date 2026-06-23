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
});
