import type { HealthEvent, HealthEventPage } from "@/lib/health/events";
import { DEFAULT_EVENTS_QUERY } from "@/lib/health/events";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { HealthEventsView, type HealthEventsLoaders } from "./health-events-view";

const base: HealthEvent = {
  id: "e1", subjectType: "point", subjectId: "P1", subjectName: "給気温度", buildingName: "本館",
  kind: "stale", severity: "warn", raisedAt: "2026-10-02T12:00:00Z", clearedAt: null, isOpen: true,
  acknowledgedAt: null, acknowledgedBy: null,
  detail: { ageSeconds: 900, thresholdSeconds: 300, lastSeen: null, reason: null, value: null, violated: null, pointCount: null },
};
const page = (items: HealthEvent[], total = items.length): HealthEventPage => ({ items, total, limit: 50, offset: 0 });

function setup(items: HealthEvent[], over: Partial<HealthEventsLoaders> = {}, props: Partial<React.ComponentProps<typeof HealthEventsView>> = {}) {
  const loaders: HealthEventsLoaders = {
    loadEvents: vi.fn().mockResolvedValue(page(items)),
    acknowledge: vi.fn(),
    ...over,
  };
  const onQueryChange = vi.fn();
  render(<HealthEventsView loaders={loaders} canAcknowledge onQueryChange={onQueryChange} {...props} />);
  return { loaders, onQueryChange };
}

describe("HealthEventsView (#455)", () => {
  it("lists events with severity, kind, target, a plain-language summary and the open state", async () => {
    setup([base]);
    const row = await screen.findByTestId("event-row-e1");
    expect(within(row).getByTestId("event-severity")).toHaveTextContent("警告");
    expect(within(row).getByTestId("event-kind")).toHaveTextContent("鮮度切れ");
    expect(within(row).getByText("給気温度")).toBeInTheDocument();
    expect(within(row).getByText(/最終受信から 15分/)).toBeInTheDocument();
    expect(within(row).getByTestId("event-cleared")).toHaveTextContent("未解消");
    expect(screen.getByTestId("events-total")).toHaveTextContent("1 件");
  });

  it("links a point to its detail and shows a gateway by its id", async () => {
    setup([base, { ...base, id: "e2", subjectType: "gateway", subjectId: "GW-1", subjectName: "GW-1", kind: "gateway_offline", severity: "critical" }]);
    expect(await screen.findByRole("link", { name: "給気温度" })).toHaveAttribute("href", "/points/P1");
    expect(within(screen.getByTestId("event-row-e2")).queryByRole("link")).toBeNull();
  });

  it("shows a cleared + acknowledged row: lifecycle and acknowledgement are independent", async () => {
    setup([{ ...base, isOpen: false, clearedAt: "2026-10-02T13:00:00Z", acknowledgedAt: "2026-10-02T13:30:00Z", acknowledgedBy: "山田" }]);
    const row = await screen.findByTestId("event-row-e1");
    expect(within(row).getByTestId("event-cleared")).not.toHaveTextContent("未解消");
    expect(within(row).getByTestId("event-ack")).toHaveTextContent("山田");
    expect(within(row).queryByTestId("event-ack-button")).toBeNull();
  });

  it("acknowledging replaces the row with the server's answer", async () => {
    const acknowledge = vi.fn().mockResolvedValue({ ...base, acknowledgedAt: "2026-10-02T12:10:00Z", acknowledgedBy: "鈴木" });
    setup([base], { acknowledge });

    fireEvent.click(await screen.findByTestId("event-ack-button"));

    await waitFor(() => expect(screen.getByTestId("event-ack")).toHaveTextContent("鈴木"));
    expect(acknowledge).toHaveBeenCalledWith("e1");
    expect(screen.queryByTestId("event-ack-button")).toBeNull();
  });

  it("a failed acknowledgement keeps the button and says so", async () => {
    setup([base], { acknowledge: vi.fn().mockRejectedValue(new Error("403")) });

    fireEvent.click(await screen.findByTestId("event-ack-button"));

    expect(await screen.findByTestId("events-ack-error")).toHaveTextContent("403");
    expect(screen.getByTestId("event-ack-button")).toBeInTheDocument();
  });

  it("offers no acknowledge button without permission", async () => {
    setup([base], {}, { canAcknowledge: false });
    const row = await screen.findByTestId("event-row-e1");
    expect(within(row).queryByTestId("event-ack-button")).toBeNull();
    expect(within(row).getByTestId("event-ack")).toHaveTextContent("未確認");
  });

  it("lifecycle and ack are separate filters, each reported to the parent with page 1", async () => {
    const { onQueryChange } = setup([base], {}, { query: { ...DEFAULT_EVENTS_QUERY, offset: 100 } });
    await screen.findByTestId("event-row-e1");

    fireEvent.click(screen.getByTestId("events-lifecycle-cleared"));
    expect(onQueryChange).toHaveBeenLastCalledWith(expect.objectContaining({ lifecycle: "cleared", ack: "", offset: 0 }));

    fireEvent.click(screen.getByTestId("events-ack-unacked"));
    expect(onQueryChange).toHaveBeenLastCalledWith(expect.objectContaining({ lifecycle: "open", ack: "unacked" }));

    fireEvent.click(screen.getByTestId("events-kind-alarm"));
    expect(onQueryChange).toHaveBeenLastCalledWith(expect.objectContaining({ kinds: ["alarm"] }));
  });

  it("says so when nothing matches, and when the fetch fails", async () => {
    setup([]);
    expect(await screen.findByTestId("events-empty")).toBeInTheDocument();
  });

  it("shows a fetch error as a banner", async () => {
    setup([], { loadEvents: vi.fn().mockRejectedValue(new Error("boom")) });
    expect(await screen.findByTestId("events-error")).toHaveTextContent("boom");
  });

  it("pages forward with the page size as the offset step", async () => {
    const items = [base];
    const { onQueryChange } = setup(items, { loadEvents: vi.fn().mockResolvedValue(page(items, 120)) });
    await screen.findByTestId("events-page");

    fireEvent.click(screen.getByRole("button", { name: "次へ" }));

    expect(onQueryChange).toHaveBeenLastCalledWith(expect.objectContaining({ offset: 50 }));
    expect(screen.getByRole("button", { name: "前へ" })).toBeDisabled();
  });
});
