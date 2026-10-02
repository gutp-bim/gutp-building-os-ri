import type { HealthEvent } from "@/lib/health/events";
import { render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { PointHealthEvents } from "./point-health-events";

const ev: HealthEvent = {
  id: "e1", subjectType: "point", subjectId: "P1", subjectName: "給気温度", buildingName: null,
  kind: "stale", severity: "warn", raisedAt: "2026-10-02T12:00:00Z", clearedAt: null, isOpen: true,
  acknowledgedAt: null, acknowledgedBy: null,
  detail: { ageSeconds: 900, thresholdSeconds: 300, lastSeen: null, reason: null, value: null, violated: null, pointCount: null },
};

describe("PointHealthEvents (#455)", () => {
  it("asks for this point's events, newest first, any lifecycle and acknowledgement", async () => {
    const load = vi.fn().mockResolvedValue({ items: [ev], total: 1, limit: 5, offset: 0 });
    render(<PointHealthEvents pointId="P1" load={load} />);

    await screen.findByTestId("point-event-e1");
    expect(load).toHaveBeenCalledWith(
      expect.objectContaining({ subjectType: "point", subjectId: "P1", lifecycle: "", ack: "", limit: 5 }),
    );
  });

  it("shows the kind, a summary, that it is still open, and who acknowledged a cleared one", async () => {
    const cleared = { ...ev, id: "e2", isOpen: false, clearedAt: "2026-10-02T13:00:00Z", acknowledgedAt: "2026-10-02T13:30:00Z", acknowledgedBy: "山田" };
    render(<PointHealthEvents pointId="P1" load={vi.fn().mockResolvedValue({ items: [ev, cleared], total: 2, limit: 5, offset: 0 })} />);

    const open = await screen.findByTestId("point-event-e1");
    expect(open).toHaveTextContent("鮮度切れ");
    expect(open).toHaveTextContent("最終受信から 15分");
    expect(open).toHaveTextContent("未解消");
    expect(screen.getByTestId("point-event-e2")).toHaveTextContent("山田 が確認");
  });

  it("says there are none, and renders nothing when the fetch fails", async () => {
    const { unmount } = render(<PointHealthEvents pointId="P1" load={vi.fn().mockResolvedValue({ items: [], total: 0, limit: 5, offset: 0 })} />);
    expect(await screen.findByTestId("point-events-empty")).toBeInTheDocument();
    unmount();

    const { container } = render(<PointHealthEvents pointId="P1" load={vi.fn().mockRejectedValue(new Error("x"))} />);
    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });
});
