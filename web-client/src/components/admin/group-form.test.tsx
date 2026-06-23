import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { GroupForm } from "./group-form";

describe("GroupForm (create)", () => {
  it("blocks submit and shows an error when id/name are missing", () => {
    const onSubmit = vi.fn();
    render(<GroupForm mode="create" onSubmit={onSubmit} />);
    fireEvent.submit(screen.getByTestId("group-form"));
    expect(onSubmit).not.toHaveBeenCalled();
    expect(screen.getByTestId("group-form-error")).toHaveTextContent("必須");
  });

  it("rejects an id with disallowed characters", () => {
    const onSubmit = vi.fn();
    render(<GroupForm mode="create" onSubmit={onSubmit} />);
    fireEvent.change(screen.getByLabelText("ID *"), { target: { value: "bad id" } });
    fireEvent.change(screen.getByLabelText("名前 *"), { target: { value: "X" } });
    fireEvent.submit(screen.getByTestId("group-form"));
    expect(onSubmit).not.toHaveBeenCalled();
    expect(screen.getByTestId("group-form-error")).toHaveTextContent("英数字");
  });

  it("submits trimmed values when valid", () => {
    const onSubmit = vi.fn();
    render(<GroupForm mode="create" onSubmit={onSubmit} />);
    fireEvent.change(screen.getByLabelText("ID *"), { target: { value: " team-1 " } });
    fireEvent.change(screen.getByLabelText("名前 *"), { target: { value: " HVAC " } });
    fireEvent.submit(screen.getByTestId("group-form"));
    expect(onSubmit).toHaveBeenCalledWith({ id: "team-1", name: "HVAC", description: "" });
  });
});

describe("GroupForm (edit)", () => {
  it("makes the id read-only and pre-fills initial values", () => {
    const onSubmit = vi.fn();
    render(
      <GroupForm
        mode="edit"
        initial={{ id: "g1", name: "Old", description: "desc" }}
        onSubmit={onSubmit}
      />,
    );
    expect(screen.getByLabelText("ID")).toHaveAttribute("readonly");
    expect(screen.getByLabelText("名前 *")).toHaveValue("Old");
    fireEvent.change(screen.getByLabelText("名前 *"), { target: { value: "New" } });
    fireEvent.submit(screen.getByTestId("group-form"));
    expect(onSubmit).toHaveBeenCalledWith({ id: "g1", name: "New", description: "desc" });
  });

  it("surfaces a submit error from the parent", () => {
    render(<GroupForm mode="edit" initial={{ id: "g1", name: "X" }} submitError="boom" onSubmit={vi.fn()} />);
    expect(screen.getByTestId("group-form-error")).toHaveTextContent("boom");
  });
});
