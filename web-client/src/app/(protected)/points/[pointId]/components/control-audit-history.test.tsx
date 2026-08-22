import { render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ControlAuditEntry } from "@/lib/control-audit/types";
import { ControlAuditHistory } from "./control-audit-history";

const entries: ControlAuditEntry[] = [
  {
    controlId: "c1",
    pointId: "PT001",
    request: '{"value":21.5}',
    status: "success",
    createdAt: "2026-07-15T00:00:00Z",
    completedAt: "2026-07-15T00:00:01Z",
  },
  {
    controlId: "c2",
    pointId: "PT001",
    request: '{"value":18}',
    status: "failed",
    createdAt: "2026-07-14T00:00:00Z",
    completedAt: "2026-07-14T00:00:02Z",
  },
  {
    controlId: "c3",
    pointId: "PT001",
    request: '{"value":22}',
    status: "pending",
    createdAt: "2026-07-13T00:00:00Z",
    completedAt: null,
  },
];

describe("ControlAuditHistory", () => {
  it("refetches when reloadKey changes, so a control just issued appears without a reload", async () => {
    // The audit row is written server-side while the operator is still on the page (#333), so the
    // panel must be told to look again — otherwise it stays frozen at page-load state.
    const load = vi.fn().mockResolvedValue([]);
    const { rerender } = render(
      <ControlAuditHistory pointId="PT001" load={load} reloadKey={0} />,
    );
    await screen.findByTestId("control-audit-empty");
    expect(load).toHaveBeenCalledTimes(1);

    load.mockResolvedValue(entries);
    rerender(<ControlAuditHistory pointId="PT001" load={load} reloadKey={1} />);

    await waitFor(() => expect(load).toHaveBeenCalledTimes(2));
    expect(await screen.findAllByTestId("control-audit-row")).toHaveLength(3);
  });

  it("retries once when the refreshed history still shows the command as 実行中", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const pending: ControlAuditEntry[] = [
        { ...entries[0], status: "pending", completedAt: null },
      ];
      const load = vi
        .fn()
        .mockResolvedValueOnce(pending) // page load
        .mockResolvedValueOnce(pending) // refetch on settle — result write has not landed yet
        .mockResolvedValue(entries); // retry sees the committed outcome
      const { rerender } = render(
        <ControlAuditHistory pointId="PT001" load={load} reloadKey={0} />,
      );
      await screen.findAllByTestId("control-audit-row");

      rerender(<ControlAuditHistory pointId="PT001" load={load} reloadKey={1} />);
      await waitFor(() => expect(load).toHaveBeenCalledTimes(2));

      await vi.advanceTimersByTimeAsync(2_000);
      await waitFor(() => expect(load).toHaveBeenCalledTimes(3));
      expect(await screen.findByTestId("control-audit-status-success")).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it("keeps the current rows visible while refetching, so the anchor does not move", async () => {
    let resolveSecond: (v: ControlAuditEntry[]) => void = () => {};
    const load = vi
      .fn()
      .mockResolvedValueOnce(entries)
      .mockImplementationOnce(() => new Promise((r) => (resolveSecond = r)));
    const { rerender } = render(
      <ControlAuditHistory pointId="PT001" load={load} reloadKey={0} />,
    );
    await screen.findAllByTestId("control-audit-row");

    rerender(<ControlAuditHistory pointId="PT001" load={load} reloadKey={1} />);

    // Still showing the previous rows rather than collapsing to 読み込み中…
    expect(screen.getAllByTestId("control-audit-row")).toHaveLength(3);
    resolveSecond(entries);
  });

  it("does not refetch when reloadKey is unchanged", async () => {
    const load = vi.fn().mockResolvedValue([]);
    const { rerender } = render(
      <ControlAuditHistory pointId="PT001" load={load} reloadKey={3} />,
    );
    await screen.findByTestId("control-audit-empty");

    rerender(<ControlAuditHistory pointId="PT001" load={load} reloadKey={3} />);

    expect(load).toHaveBeenCalledTimes(1);
  });

  it("renders one row per audit entry with a status badge and command value", async () => {
    const load = vi.fn().mockResolvedValue(entries);
    render(<ControlAuditHistory pointId="PT001" load={load} />);

    const rows = await screen.findAllByTestId("control-audit-row");
    expect(rows).toHaveLength(3);
    expect(screen.getByTestId("control-audit-status-success")).toHaveTextContent("成功");
    expect(screen.getByTestId("control-audit-status-failed")).toHaveTextContent("失敗");
    expect(screen.getByTestId("control-audit-status-pending")).toHaveTextContent("実行中");
    expect(rows[0]).toHaveTextContent("値 21.5");
    expect(load).toHaveBeenCalledWith("PT001");
  });

  it("shows the empty state when there is no history", async () => {
    render(<ControlAuditHistory pointId="PT001" load={vi.fn().mockResolvedValue([])} />);
    expect(await screen.findByTestId("control-audit-empty")).toBeInTheDocument();
  });

  it("shows an error message when the load fails", async () => {
    render(
      <ControlAuditHistory
        pointId="PT001"
        load={vi.fn().mockRejectedValue(new Error("制御履歴の取得に失敗しました (403)"))}
      />,
    );
    expect(await screen.findByTestId("control-audit-error")).toHaveTextContent("403");
  });

  it("renders an em dash for an in-flight command's completion time", async () => {
    const load = vi.fn().mockResolvedValue([entries[2]]);
    render(<ControlAuditHistory pointId="PT001" load={load} />);
    const row = await screen.findByTestId("control-audit-row");
    await waitFor(() => expect(row).toHaveTextContent("—"));
  });
});
