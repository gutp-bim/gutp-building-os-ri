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
