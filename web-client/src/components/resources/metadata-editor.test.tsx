import type { ResourceMetadata } from "@/lib/resources/types";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { MetadataEditor } from "./metadata-editor";

const base: ResourceMetadata = {
  identifiers: { ifcGuid: "3Skg8nAD" },
  customTags: { geometryMapped: true },
};

describe("MetadataEditor", () => {
  it("renders existing identifiers and customTags", () => {
    render(<MetadataEditor metadata={base} onSave={vi.fn()} onCancel={vi.fn()} />);
    expect(screen.getByDisplayValue("ifcGuid")).toBeInTheDocument();
    expect(screen.getByDisplayValue("3Skg8nAD")).toBeInTheDocument();
    expect(screen.getByDisplayValue("geometryMapped")).toBeInTheDocument();
  });

  it("calls onSave with modified identifier", async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<MetadataEditor metadata={base} onSave={onSave} onCancel={vi.fn()} />);

    const valInput = screen.getByDisplayValue("3Skg8nAD");
    fireEvent.change(valInput, { target: { value: "NEW-GUID" } });
    fireEvent.click(screen.getByTestId("metadata-save-btn"));

    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    const patch = onSave.mock.calls[0][0];
    expect(patch.identifiers?.ifcGuid).toBe("NEW-GUID");
  });

  it("marks deleted identifier key as null in patch", async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<MetadataEditor metadata={base} onSave={onSave} onCancel={vi.fn()} />);

    fireEvent.click(screen.getAllByTestId("delete-ident-row")[0]);
    fireEvent.click(screen.getByTestId("metadata-save-btn"));

    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    const patch = onSave.mock.calls[0][0];
    expect(patch.identifiers?.ifcGuid).toBeNull();
  });

  it("calls onCancel when cancel button clicked", () => {
    const onCancel = vi.fn();
    render(<MetadataEditor metadata={base} onSave={vi.fn()} onCancel={onCancel} />);
    fireEvent.click(screen.getByTestId("metadata-cancel-btn"));
    expect(onCancel).toHaveBeenCalledOnce();
  });

  it("adds a new identifier row when add button clicked", () => {
    render(<MetadataEditor metadata={base} onSave={vi.fn()} onCancel={vi.fn()} />);
    fireEvent.click(screen.getByTestId("add-ident-btn"));
    // one extra key input should appear
    expect(screen.getAllByPlaceholderText("key").length).toBeGreaterThan(1);
  });
});
