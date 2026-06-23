import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { PermissionEditor } from "./permission-editor";

describe("PermissionEditor", () => {
  it("renders existing permissions with remove buttons", () => {
    const onRemove = vi.fn();
    render(
      <PermissionEditor permissions={["d:abc:rw"]} onAdd={vi.fn()} onRemove={onRemove} />,
    );
    const row = screen.getByTestId("permission-row-d:abc:rw");
    expect(row).toHaveTextContent("device");
    expect(row).toHaveTextContent("読み取り");
    expect(row).toHaveTextContent("書き込み");
    fireEvent.click(screen.getByRole("button", { name: "権限 d:abc:rw を削除" }));
    expect(onRemove).toHaveBeenCalledWith("d:abc:rw");
  });

  it("renders the resolved display name instead of the raw hash when provided", () => {
    render(
      <PermissionEditor
        permissions={["d:hash1:r"]}
        resolved={{ hash1: { originalId: "dev-1", displayName: "1F AHU" } }}
        onAdd={vi.fn()}
        onRemove={vi.fn()}
      />,
    );
    const row = screen.getByTestId("permission-row-d:hash1:r");
    expect(row).toHaveTextContent("1F AHU");
    expect(row).not.toHaveTextContent("hash1");
  });

  it("shows the empty state with no permissions", () => {
    render(<PermissionEditor permissions={[]} onAdd={vi.fn()} onRemove={vi.fn()} />);
    expect(screen.getByTestId("permissions-empty")).toBeInTheDocument();
  });

  it("validates the add form before calling onAdd", () => {
    const onAdd = vi.fn();
    render(<PermissionEditor permissions={[]} onAdd={onAdd} onRemove={vi.fn()} />);
    // resourceId empty → validation error, onAdd not called
    fireEvent.click(screen.getByRole("button", { name: "追加" }));
    expect(onAdd).not.toHaveBeenCalled();
    expect(screen.getByTestId("permission-add-error")).toBeInTheDocument();
  });

  it("builds the abbreviated permission string on add", () => {
    const onAdd = vi.fn();
    render(<PermissionEditor permissions={[]} onAdd={onAdd} onRemove={vi.fn()} />);
    fireEvent.change(screen.getByLabelText("リソース種別"), { target: { value: "building" } });
    fireEvent.change(screen.getByLabelText("リソース ID"), { target: { value: "bldg-1" } });
    // default action is read; add write
    fireEvent.click(screen.getByLabelText("書き込み"));
    fireEvent.click(screen.getByRole("button", { name: "追加" }));
    expect(onAdd).toHaveBeenCalledWith("b:bldg-1:rw");
  });
});
