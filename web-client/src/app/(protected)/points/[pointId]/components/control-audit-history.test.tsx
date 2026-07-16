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
  it("renders one row per audit entry with a status badge and command value", async () => {
    const load = vi.fn().mockResolvedValue(entries);
    render(<ControlAuditHistory pointId="PT001" load={load} />);

    const rows = await screen.findAllByTestId("control-audit-row");
    expect(rows).toHaveLength(3);
    expect(screen.getByTestId("control-status-success")).toHaveTextContent("成功");
    expect(screen.getByTestId("control-status-failed")).toHaveTextContent("失敗");
    expect(screen.getByTestId("control-status-pending")).toHaveTextContent("実行中");
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
