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
    render(
      <ColdDataDownloadModal
        {...base}
        error="CSV のダウンロードに失敗しました。"
      />,
    );
    expect(screen.getByTestId("cold-download-error")).toHaveTextContent(
      "CSV のダウンロードに失敗しました。",
    );
  });

  it("shows no error banner by default", () => {
    render(<ColdDataDownloadModal {...base} />);
    expect(screen.queryByTestId("cold-download-error")).not.toBeInTheDocument();
  });
});

describe("ColdDataDownloadModal range guard (#197)", () => {
  const download = () => screen.getByRole("button", { name: /ダウンロード/ });

  it("disables download while the range is incomplete (no guard message yet)", () => {
    render(<ColdDataDownloadModal {...base} />);
    expect(download()).toBeDisabled();
    expect(
      screen.queryByTestId("cold-download-range-error"),
    ).not.toBeInTheDocument();
  });

  it("warns and disables download when start is not before end", () => {
    render(
      <ColdDataDownloadModal
        {...base}
        startDate="2026-07-17T10:00"
        endDate="2026-07-17T09:00"
      />,
    );
    expect(screen.getByTestId("cold-download-range-error")).toHaveTextContent(
      "開始日時は終了日時より前にしてください。",
    );
    expect(download()).toBeDisabled();
  });

  it("warns and disables download for a future end date", () => {
    render(
      <ColdDataDownloadModal
        {...base}
        startDate="2099-01-01T00:00"
        endDate="2099-01-02T00:00"
      />,
    );
    expect(screen.getByTestId("cold-download-range-error")).toHaveTextContent(
      "終了日時に未来の日時は指定できません。",
    );
    expect(download()).toBeDisabled();
  });

  it("enables download for a complete, valid, past range", () => {
    render(
      <ColdDataDownloadModal
        {...base}
        startDate="2020-01-01T00:00"
        endDate="2020-01-02T00:00"
      />,
    );
    expect(
      screen.queryByTestId("cold-download-range-error"),
    ).not.toBeInTheDocument();
    expect(download()).toBeEnabled();
  });
});
