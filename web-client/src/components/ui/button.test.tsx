import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { Button } from "./button";

describe("Button (#194)", () => {
  it("defaults to type=button (never submits a form implicitly) and the primary variant", () => {
    render(<Button>保存</Button>);
    const btn = screen.getByRole("button", { name: "保存" });
    expect(btn).toHaveAttribute("type", "button");
    expect(btn).toHaveClass("bg-blue-600");
  });

  it("applies the requested variant + size and merges caller classes", () => {
    render(
      <Button variant="danger" size="sm" className="w-full">
        削除
      </Button>,
    );
    const btn = screen.getByRole("button", { name: "削除" });
    expect(btn).toHaveClass("bg-red-600", "px-3", "py-1", "w-full");
  });

  it("forwards native button props (onClick, disabled)", () => {
    const onClick = vi.fn();
    render(
      <Button onClick={onClick} disabled>
        送信
      </Button>,
    );
    const btn = screen.getByRole("button", { name: "送信" });
    expect(btn).toBeDisabled();
    fireEvent.click(btn);
    expect(onClick).not.toHaveBeenCalled();
  });
});
