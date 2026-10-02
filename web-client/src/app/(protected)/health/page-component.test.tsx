import type { HealthQuery } from "@/lib/health/query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

const { replace, searchParams } = vi.hoisted(() => ({
  replace: vi.fn(),
  searchParams: { value: new URLSearchParams() },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace }),
  useSearchParams: () => searchParams.value,
}));

// 本番ローダは API を叩くので、結線の関心（URL ⇄ 条件）だけを見るために差し替える。
vi.mock("@/lib/health/loaders", () => ({ productionHealthLoaders: {} }));

// ビューは注入された条件をそのまま表示し、押されたら次の条件を返すだけのスタブにする。
vi.mock("@/components/health/data-health-view", () => ({
  DataHealthView: ({
    query,
    onQueryChange,
  }: {
    query: HealthQuery;
    onQueryChange: (next: HealthQuery) => void;
  }) => (
    <div>
      <span data-testid="freshness">{query.freshness.join(",")}</span>
      <span data-testid="building">{query.buildingDtId ?? ""}</span>
      <button
        data-testid="pick-missing"
        onClick={() => onQueryChange({ ...query, freshness: ["missing"] })}
      >
        欠測
      </button>
      <button
        data-testid="clear"
        onClick={() => onQueryChange({ ...query, freshness: [] })}
      >
        すべて
      </button>
    </div>
  ),
}));

vi.mock("js-cookie", () => ({ default: { get: () => undefined } }));
vi.mock("@/components/health/health-events-view", () => ({
  HealthEventsView: ({
    query,
    canAcknowledge,
    onQueryChange,
  }: {
    query: { lifecycle: string; ack: string };
    canAcknowledge: boolean;
    onQueryChange: (q: unknown) => void;
  }) => (
    <div data-testid="events-stub">
      <span data-testid="lifecycle">{query.lifecycle}</span>
      <span data-testid="can-ack">{String(canAcknowledge)}</span>
      <button data-testid="pick-cleared" onClick={() => onQueryChange({ ...query, lifecycle: "cleared", ack: "", kinds: [], limit: 50, offset: 0 })}>
        cleared
      </button>
    </div>
  ),
}));

import HealthPageComponent from "./page-component";

afterEach(() => {
  replace.mockReset();
  searchParams.value = new URLSearchParams();
});

describe("HealthPageComponent", () => {
  it("URL クエリを検索条件として読み込む", () => {
    searchParams.value = new URLSearchParams(
      "freshness=stale,missing&buildingDtId=dtmi%3Ab1",
    );

    render(<HealthPageComponent />);

    expect(screen.getByTestId("freshness").textContent).toBe("stale,missing");
    expect(screen.getByTestId("building").textContent).toBe("dtmi:b1");
  });

  it("条件が変わったら履歴を積まずに URL を置き換える", async () => {
    render(<HealthPageComponent />);

    await userEvent.click(screen.getByTestId("pick-missing"));

    expect(replace).toHaveBeenCalledWith("/health?freshness=missing");
  });

  it("既定の条件に戻ったらクエリなしの URL にする", async () => {
    searchParams.value = new URLSearchParams("freshness=missing");

    render(<HealthPageComponent />);
    await userEvent.click(screen.getByTestId("clear"));

    expect(replace).toHaveBeenCalledWith("/health");
  });
});

describe("HealthPageComponent events tab (#455)", () => {
  it("shows the state view by default, with both tabs", () => {
    render(<HealthPageComponent />);
    expect(screen.getByTestId("freshness")).toBeInTheDocument();
    expect(screen.queryByTestId("events-stub")).toBeNull();
    expect(screen.getByTestId("health-tab-state")).toHaveAttribute("aria-current", "page");
    expect(screen.getByTestId("health-tab-events")).toHaveAttribute("href", "/health?view=events");
  });

  it("view=events shows the events, reading their filters from the URL", () => {
    searchParams.value = new URLSearchParams("view=events&lifecycle=cleared");
    render(<HealthPageComponent />);
    expect(screen.getByTestId("lifecycle")).toHaveTextContent("cleared");
    expect(screen.queryByTestId("freshness")).toBeNull();
    expect(screen.getByTestId("health-tab-events")).toHaveAttribute("aria-current", "page");
  });

  it("writes event filters back to the URL, keeping view=events", async () => {
    searchParams.value = new URLSearchParams("view=events");
    render(<HealthPageComponent />);
    await userEvent.click(screen.getByTestId("pick-cleared"));
    const url = replace.mock.calls.at(-1)?.[0] as string;
    expect(url).toContain("view=events");
    expect(url).toContain("lifecycle=cleared");
  });

  it("hides the acknowledge action without an acknowledging role", () => {
    searchParams.value = new URLSearchParams("view=events");
    render(<HealthPageComponent />);
    expect(screen.getByTestId("can-ack")).toHaveTextContent("false");
  });
});
