import { render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const deviceGet = vi.fn();
const pointsGet = vi.fn();
vi.mock("@/lib/infra/aspida-client", () => ({
  apiClient: () => ({
    devices: { _deviceDtId: () => ({ $get: deviceGet }) },
    points: { $get: pointsGet },
  }),
}));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ back: vi.fn(), push: vi.fn() }),
}));
vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

import { TableProvider } from "@/contexts/TableContext";
import DeviceDetailPageComponent from "./page-component";

function renderPage() {
  return render(
    <TableProvider
      fields={[
        "name",
        "dataSpecification",
        "dataType",
        "writable",
        "targetArea",
      ]}
    >
      <DeviceDetailPageComponent deviceId="urn:dev:1" />
    </TableProvider>,
  );
}

// twin (sbco:owner / site / supplier / deviceType) が持っている値を、API がそのまま返す形。
// 以前のフィクスチャは owner/site/supplier を手で与え deviceType だけ空文字にしていたため、
// 「twin にあるのにバックエンドが返していない」状態をこのテストは一切捕捉できていなかった (#298)。
const device = {
  id: "device:1",
  dtId: "urn:dev:1",
  name: "AHU-1",
  owner: "Building Management",
  site: "site-1",
  supplier: "VendorA",
  gatewayId: "GW-SOS-001",
  deviceType: "Sensor",
};

// 未配線だった頃の応答＝記述メタデータが一切付かない形。
const deviceWithoutMetadata = {
  id: "device:1",
  dtId: "urn:dev:1",
  name: "AHU-1",
  gatewayId: "GW-SOS-001",
};

beforeEach(() => {
  deviceGet.mockReset();
  pointsGet.mockReset();
});

describe("DeviceDetailPageComponent (#195)", () => {
  it("shows a text loading state, then the device info and keyboard-accessible point links", async () => {
    deviceGet.mockResolvedValueOnce(device);
    pointsGet.mockResolvedValueOnce([
      {
        id: "point:1",
        name: "室温",
        specification: "spec",
        type: "float",
        writable: false,
        targetArea: "R1",
      },
    ]);
    renderPage();

    expect(screen.getByText("読み込み中…")).toBeInTheDocument();

    const row = await screen.findByTestId("device-point-row");
    const link = within(row).getByRole("link", { name: "室温" });
    expect(link.tagName).toBe("A");
    expect(link).toHaveAttribute("href", "/points/point%3A1");
    expect(screen.getByText("AHU-1")).toBeInTheDocument();
  });

  it("renders the descriptive metadata the twin holds (#298)", async () => {
    deviceGet.mockResolvedValueOnce(device);
    pointsGet.mockResolvedValueOnce([]);
    renderPage();

    expect(
      await screen.findByText("Owner : Building Management"),
    ).toBeInTheDocument();
    expect(screen.getByText("Site : site-1")).toBeInTheDocument();
    expect(screen.getByText("Supplier : VendorA")).toBeInTheDocument();
    expect(screen.getByText("Device Type : Sensor")).toBeInTheDocument();
  });

  it("says 未登録 instead of leaving the label trailing into nothing", async () => {
    deviceGet.mockResolvedValueOnce(deviceWithoutMetadata);
    pointsGet.mockResolvedValueOnce([]);
    renderPage();

    expect(await screen.findByText("Owner : 未登録")).toBeInTheDocument();
    expect(screen.getByText("Supplier : 未登録")).toBeInTheDocument();
    expect(screen.getByText("Device Type : 未登録")).toBeInTheDocument();
  });

  it("shows an inline error banner when the fetch fails", async () => {
    deviceGet.mockRejectedValueOnce(new Error("boom"));
    pointsGet.mockRejectedValueOnce(new Error("boom"));
    renderPage();

    expect(await screen.findByTestId("inline-banner-error")).toHaveTextContent(
      "デバイス情報の取得に失敗しました。",
    );
  });

  // The データ型 cell reads the point's measurement kind. On the domain type `type` is the resource
  // discriminator (always "point"), so a migration that leaves `.point.type` in place still compiles
  // and renders "point" in every row — the trap documented on `PointResource.kind`. No assertion
  // covered this cell before, which is exactly why it slipped through.
  it("renders the measurement kind in データ型, not the resource discriminator", async () => {
    deviceGet.mockResolvedValueOnce(device);
    pointsGet.mockResolvedValueOnce([
      { id: "point:1", name: "室温", type: "float", targetArea: "R1" },
    ]);
    renderPage();

    expect(await screen.findByText("float")).toBeInTheDocument();
    expect(screen.queryByText("point")).not.toBeInTheDocument();
  });
});
