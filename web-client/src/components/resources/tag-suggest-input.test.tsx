import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TagSuggestInput } from "./tag-suggest-input";

describe("TagSuggestInput (#454)", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function setup(
    suggest = vi.fn().mockResolvedValue([{ tag: "temperature", count: 12 }]),
  ) {
    const onChange = vi.fn();
    const utils = render(
      <TagSuggestInput
        value=""
        onChange={onChange}
        suggest={suggest}
        ariaLabel="タグ"
      />,
    );
    return { suggest, onChange, ...utils };
  }

  it("does not ask for candidates below 2 characters", async () => {
    const { suggest } = setup();
    fireEvent.change(screen.getByLabelText("タグ"), { target: { value: "t" } });
    await act(() => vi.advanceTimersByTimeAsync(500));
    expect(suggest).not.toHaveBeenCalled();
  });

  it("debounces, asks with the typed prefix and lists candidates with their counts", async () => {
    const suggest = vi
      .fn()
      .mockResolvedValue([{ tag: "temperature", count: 12 }]);
    const { rerender } = render(
      <TagSuggestInput
        value="te"
        onChange={vi.fn()}
        suggest={suggest}
        ariaLabel="タグ"
      />,
    );
    rerender(
      <TagSuggestInput
        value="tem"
        onChange={vi.fn()}
        suggest={suggest}
        ariaLabel="タグ"
      />,
    );
    await act(() => vi.advanceTimersByTimeAsync(500));

    expect(suggest).toHaveBeenCalledTimes(1);
    expect(suggest).toHaveBeenCalledWith("tem");
    const option = document.querySelector(
      "datalist option",
    ) as HTMLOptionElement;
    expect(option.value).toBe("temperature");
    expect(option.label).toContain("12");
  });

  it("swallows a failed lookup — typing a tag by hand still works", async () => {
    const suggest = vi.fn().mockRejectedValue(new Error("boom"));
    render(
      <TagSuggestInput
        value="tem"
        onChange={vi.fn()}
        suggest={suggest}
        ariaLabel="タグ"
      />,
    );
    await act(() => vi.advanceTimersByTimeAsync(500));
    expect(suggest).toHaveBeenCalled();
    expect(document.querySelectorAll("datalist option")).toHaveLength(0);
    expect(screen.getByLabelText("タグ")).toBeInTheDocument();
  });

  it("forwards typing to onChange and passes other input props through", () => {
    const { onChange } = setup();
    fireEvent.change(screen.getByLabelText("タグ"), { target: { value: "x" } });
    expect(onChange).toHaveBeenCalledWith("x");
  });
});
