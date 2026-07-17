import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ColdDataDownloadModal } from "./cold-data-download-modal";

const base = {
  isOpen: true,
  onClose: vi.fn(),
  startDate: "",
  endDate: "",
  onStartDateChange: vi.fn(),
  onEndDateChange: vi.fn(),
  onDownload: vi.fn(),
  isLoading: false,
};

describe("ColdDataDownloadModal error surfacing (#196)", () => {
  it("shows an inline error banner when a download failed", () => {
    render(<ColdDataDownloadModal {...base} error="CSV のダウンロードに失敗しました。" />);
    expect(screen.getByTestId("cold-download-error")).toHaveTextContent(
      "CSV のダウンロードに失敗しました。",
    );
  });

  it("shows no error banner by default", () => {
    render(<ColdDataDownloadModal {...base} />);
    expect(screen.queryByTestId("cold-download-error")).not.toBeInTheDocument();
  });
});
