import type { HealthLoaders } from "@/lib/health/loaders";
import type { HealthRow } from "@/lib/health/mapping";
import {
  DEFAULT_HEALTH_QUERY,
  serializeHealthQuery,
  type HealthQuery,
} from "@/lib/health/query";
import type { HealthPage, HealthSummary } from "@/lib/health/repository";
import type { ResourceRef } from "@/lib/resources/types";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi, type Mock } from "vitest";
import { DataHealthView } from "./data-health-view";

const staleRow: HealthRow = {
  pointId: "SAT-002",
  name: "給気温度",
  unit: "degC",
  freshnessStatus: "stale",
  alarmStatus: "suppressed",
  healthStatus: "stale",
  lastSeen: "2026-09-10T10:00:00Z",
  ageSeconds: 1080,
  expectedIntervalSeconds: 300,
  thresholdSeconds: 900,
  thresholdSource: "point",
  missingReason: null,
  value: 23.4,
  violated: null,
  gatewayId: "GW-001",
  gatewayConnected: true,
  deviceName: "AHU-02",
  spaceName: "会議室A",
  floorName: "1F",
  buildingName: "本館",
  tags: ["hvac"],
};

const missingRow: HealthRow = {
  ...staleRow,
  pointId: "SAT-001",
  name: "還気温度",
  freshnessStatus: "missing",
  healthStatus: "missing",
  alarmStatus: "suppressed",
  lastSeen: null,
  ageSeconds: null,
  expectedIntervalSeconds: 60,
  thresholdSeconds: 180,
  missingReason: "gatewayDisconnected",
  value: null,
  deviceName: "AHU-01",
  gatewayConnected: false,
};

const page: HealthPage = {
  rows: [missingRow, staleRow],
  total: 2,
  limit: 100,
  offset: 0,
  dataComplete: true,
  indexState: "ready",
};

const summary: HealthSummary = {
  totalPoints: 240,
  fresh: 191,
  stale: 183,
  missing: 47,
  unknown: 0,
  alarmWarn: 30,
  alarmCritical: 12,
  dataComplete: true,
  indexState: "ready",
};

const building: ResourceRef = {
  type: "building",
  dtId: "dtmi:b1",
  id: "b1",
  name: "本館",
};
const floor: ResourceRef = {
  type: "floor",
  dtId: "dtmi:f1",
  id: "f1",
  name: "1F",
};

function makeLoaders(overrides: Partial<HealthLoaders> = {}): HealthLoaders {
  return {
    loadHealth: vi.fn().mockResolvedValue(page),
    loadSummary: vi.fn().mockResolvedValue(summary),
    loadBuildings: vi.fn().mockResolvedValue([building]),
    loadFloors: vi.fn().mockResolvedValue([floor]),
    loadGatewayIds: vi.fn().mockResolvedValue(["GW-001", "GW-002"]),
    ...overrides,
  };
}

/** 条件変更の受け口。URL への写像を検証するため、呼ばれた条件をそのまま拾える形で持つ。 */
type QueryChangeMock = Mock<(next: HealthQuery) => void>;

const queryChangeMock = (): QueryChangeMock =>
  vi.fn<(next: HealthQuery) => void>();

/** 直近の `onQueryChange` を URL クエリ文字列にして比較する（URL が正本なので URL で検証する）。 */
function lastUrl(onQueryChange: QueryChangeMock): string {
  const calls = onQueryChange.mock.calls;
  const next = calls[calls.length - 1][0];
  return serializeHealthQuery(next).toString();
}

async function renderView(
  opts: {
    loaders?: HealthLoaders;
    query?: HealthQuery;
    onQueryChange?: QueryChangeMock;
  } = {},
) {
  const loaders = opts.loaders ?? makeLoaders();
  const onQueryChange = opts.onQueryChange ?? queryChangeMock();
  const view = render(
    <DataHealthView
      loaders={loaders}
      query={opts.query ?? DEFAULT_HEALTH_QUERY}
      onQueryChange={onQueryChange}
    />,
  );
  await screen.findByTestId("health-table");
  return { ...view, loaders, onQueryChange };
}

describe("DataHealthView", () => {
  it("読み込み後に 1 Point 1 行を出す", async () => {
    await renderView();

    const rows = screen.getAllByTestId("health-row");
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText("還気温度")).toBeTruthy();
    expect(within(rows[0]).getByText("SAT-001")).toBeTruthy();
    expect(within(rows[1]).getByText("給気温度")).toBeTruthy();
  });

  it("鮮度・値・Last Seen・期待周期・判定閾値・機器・Gateway を並べる", async () => {
    await renderView();

    const row = screen.getAllByTestId("health-row")[1];
    expect(within(row).getByTestId("health-freshness-stale").textContent).toBe(
      "鮮度切れ",
    );
    expect(within(row).getByText("23.4 degC")).toBeTruthy();
    expect(within(row).getByText("18分前")).toBeTruthy();
    // 期待周期 5分 / 判定閾値 15分（= 5分 × 3）。
    expect(within(row).getByText("5分")).toBeTruthy();
    expect(within(row).getByText("15分")).toBeTruthy();
    expect(within(row).getByText("AHU-02")).toBeTruthy();
    expect(within(row).getByText("GW-001")).toBeTruthy();
    expect(within(row).getByTestId("health-gateway-connected")).toBeTruthy();
  });

  it("鮮度と値異常を別の列で描く（Fresh でも値異常はあり得る）", async () => {
    const alarmRow: HealthRow = {
      ...staleRow,
      pointId: "SAT-003",
      freshnessStatus: "fresh",
      alarmStatus: "critical",
      healthStatus: "critical",
      violated: "alarmHigh",
      ageSeconds: 10,
    };
    await renderView({
      loaders: makeLoaders({
        loadHealth: vi
          .fn()
          .mockResolvedValue({ ...page, rows: [alarmRow], total: 1 }),
      }),
    });

    const row = screen.getAllByTestId("health-row")[0];
    expect(within(row).getByTestId("health-freshness-fresh").textContent).toBe(
      "最新",
    );
    expect(
      within(row).getByTestId("health-alarm-critical").textContent,
    ).toContain("異常");
  });

  it("欠測行には理由と Gateway 切断を出す", async () => {
    await renderView();

    const row = screen.getAllByTestId("health-row")[0];
    expect(
      within(row).getByTestId("health-freshness-missing").textContent,
    ).toBe("欠測");
    expect(within(row).getByText("ゲートウェイ切断")).toBeTruthy();
    expect(within(row).getByTestId("health-gateway-disconnected")).toBeTruthy();
  });

  it("行から Point 詳細へリンクする", async () => {
    await renderView();

    const link = screen.getAllByTestId("health-row-link")[0];
    expect(link.getAttribute("href")).toBe("/points/SAT-001");
  });

  it("0 件なら空状態を出す", async () => {
    await renderView({
      loaders: makeLoaders({
        loadHealth: vi.fn().mockResolvedValue({ ...page, rows: [], total: 0 }),
      }),
    });

    expect(screen.getByTestId("health-empty")).toBeTruthy();
    expect(screen.queryByTestId("health-row")).toBeNull();
  });

  it("取得に失敗したらエラーバナーを出す", async () => {
    render(
      <DataHealthView
        loaders={makeLoaders({
          loadHealth: vi
            .fn()
            .mockRejectedValue(new Error("取得できませんでした")),
        })}
      />,
    );

    const banner = await screen.findByTestId("health-error");
    expect(banner.textContent).toContain("取得できませんでした");
  });

  it("読み込み中はその旨を出す", async () => {
    render(
      <DataHealthView
        loaders={makeLoaders({
          loadHealth: vi.fn((): Promise<HealthPage> => new Promise(() => {})),
        })}
      />,
    );

    expect(screen.getByTestId("health-loading")).toBeTruthy();
  });

  it("dataComplete=false なら暫定バナーを出し、件数を確定値として見せない", async () => {
    await renderView({
      loaders: makeLoaders({
        loadHealth: vi.fn().mockResolvedValue({
          ...page,
          dataComplete: false,
          indexState: "warming",
        }),
        loadSummary: vi.fn().mockResolvedValue({
          ...summary,
          dataComplete: false,
          indexState: "warming",
        }),
      }),
    });

    expect(
      screen.getByTestId("health-incomplete-banner").textContent,
    ).toContain("同期中");
    const chip = screen.getByTestId("health-chip-missing");
    expect(chip.textContent).toContain("集計中");
    expect(chip.textContent).not.toContain("47");
  });

  it("dataComplete=true ならチップに軸別の件数を出す", async () => {
    await renderView();

    expect(screen.queryByTestId("health-incomplete-banner")).toBeNull();
    expect(screen.getByTestId("health-chip-missing").textContent).toContain(
      "47",
    );
    expect(screen.getByTestId("health-chip-stale").textContent).toContain(
      "183",
    );
    // 値異常は注意 + 異常の合計（鮮度とは別軸）。
    expect(screen.getByTestId("health-chip-alarm").textContent).toContain("42");
  });

  it("欠測チップを押すと URL が freshness=missing になる", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({ onQueryChange });

    await userEvent.click(screen.getByTestId("health-chip-missing"));

    expect(lastUrl(onQueryChange)).toBe("freshness=missing");
  });

  it("値異常チップは鮮度ではなく alarm 軸を絞る", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({ onQueryChange });

    await userEvent.click(screen.getByTestId("health-chip-alarm"));

    expect(lastUrl(onQueryChange)).toBe("alarm=warn%2Ccritical");
  });

  it("「すべて」で軸の絞り込みを外す（他の条件は残す）", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({
      onQueryChange,
      query: {
        ...DEFAULT_HEALTH_QUERY,
        freshness: ["missing"],
        alarm: ["warn"],
        buildingDtId: "dtmi:b1",
      },
    });

    await userEvent.click(screen.getByTestId("health-chip-all"));

    expect(lastUrl(onQueryChange)).toBe("buildingDtId=dtmi%3Ab1");
  });

  it("データ未受信の期間を選ぶと olderThan が付く", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({ onQueryChange });

    await userEvent.selectOptions(
      screen.getByTestId("health-older-than"),
      "600",
    );

    expect(lastUrl(onQueryChange)).toBe("olderThan=600");
  });

  it("建物を選ぶと buildingDtId が付き、フロアの選択は解除される", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({
      onQueryChange,
      query: { ...DEFAULT_HEALTH_QUERY, floorDtId: "dtmi:old" },
    });

    await userEvent.selectOptions(
      screen.getByTestId("health-building"),
      "dtmi:b1",
    );

    expect(lastUrl(onQueryChange)).toBe("buildingDtId=dtmi%3Ab1");
  });

  it("建物が選ばれていればフロアの選択肢を読み込む", async () => {
    const loaders = makeLoaders();
    render(
      <DataHealthView
        loaders={loaders}
        query={{ ...DEFAULT_HEALTH_QUERY, buildingDtId: "dtmi:b1" }}
      />,
    );

    await waitFor(() =>
      expect(loaders.loadFloors).toHaveBeenCalledWith("dtmi:b1"),
    );
    await waitFor(() =>
      expect(
        within(screen.getByTestId("health-floor")).getByRole("option", {
          name: "1F",
        }),
      ).toBeTruthy(),
    );
  });

  it("Gateway を選ぶと gatewayId が付く", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({ onQueryChange });

    await waitFor(() =>
      expect(
        within(screen.getByTestId("health-gateway")).getByRole("option", {
          name: "GW-002",
        }),
      ).toBeTruthy(),
    );
    await userEvent.selectOptions(
      screen.getByTestId("health-gateway"),
      "GW-002",
    );

    expect(lastUrl(onQueryChange)).toBe("gatewayId=GW-002");
  });

  it("検索語を入力して確定すると q が付く", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({ onQueryChange });

    await userEvent.type(screen.getByTestId("health-search"), "給気{Enter}");

    expect(lastUrl(onQueryChange)).toBe("q=%E7%B5%A6%E6%B0%97");
  });

  it("タグは外せる（他の画面からの絞り込みを解除できる）", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({
      onQueryChange,
      query: { ...DEFAULT_HEALTH_QUERY, tags: ["室温", "AHU"] },
    });

    await userEvent.click(
      screen.getByRole("button", { name: "タグ 室温 を外す" }),
    );

    expect(lastUrl(onQueryChange)).toBe("tag=AHU");
  });

  it("次頁に進むと offset が 1 頁ぶん進む", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({
      onQueryChange,
      loaders: makeLoaders({
        loadHealth: vi.fn().mockResolvedValue({ ...page, total: 250 }),
      }),
    });

    await userEvent.click(screen.getByRole("button", { name: "次へ" }));

    expect(lastUrl(onQueryChange)).toBe("offset=100");
  });

  it("絞り込みを変えたら先頭の頁に戻す（前の頁位置を持ち越さない）", async () => {
    const onQueryChange = queryChangeMock();
    await renderView({
      onQueryChange,
      query: { ...DEFAULT_HEALTH_QUERY, offset: 200 },
    });

    await userEvent.click(screen.getByTestId("health-chip-stale"));

    expect(lastUrl(onQueryChange)).toBe("freshness=stale");
  });

  it("条件が変わったら一覧と集計を取り直す", async () => {
    const loaders = makeLoaders();
    const { rerender } = render(
      <DataHealthView loaders={loaders} query={DEFAULT_HEALTH_QUERY} />,
    );
    await screen.findByTestId("health-table");
    expect(loaders.loadHealth).toHaveBeenCalledTimes(1);

    const next: HealthQuery = {
      ...DEFAULT_HEALTH_QUERY,
      freshness: ["missing"],
    };
    rerender(<DataHealthView loaders={loaders} query={next} />);

    await waitFor(() => expect(loaders.loadHealth).toHaveBeenCalledTimes(2));
    expect(loaders.loadHealth).toHaveBeenLastCalledWith(next);
    expect(loaders.loadSummary).toHaveBeenCalledTimes(2);
  });

  it("集計だけ失敗しても一覧は出す（チップの件数を落とすだけにする）", async () => {
    await renderView({
      loaders: makeLoaders({
        loadSummary: vi.fn().mockRejectedValue(new Error("集計に失敗")),
      }),
    });

    expect(screen.getAllByTestId("health-row")).toHaveLength(2);
    expect(screen.getByTestId("health-chip-missing").textContent).toContain(
      "—",
    );
  });
});
