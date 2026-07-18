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
      fields={["name", "dataSpecification", "dataType", "writable", "targetArea"]}
    >
      <DeviceDetailPageComponent deviceId="urn:dev:1" />
    </TableProvider>,
  );
}

const device = {
  id: "device:1",
  dtId: "urn:dev:1",
  name: "AHU-1",
  owner: "o",
  site: "s",
  supplier: "sup",
  gatewayId: "GW-SOS-001",
  deviceType: "",
};

beforeEach(() => {
  deviceGet.mockReset();
  pointsGet.mockReset();
});

describe("DeviceDetailPageComponent (#195)", () => {
  it("shows a text loading state, then the device info and keyboard-accessible point links", async () => {
    deviceGet.mockResolvedValueOnce(device);
    pointsGet.mockResolvedValueOnce([
      { id: "point:1", name: "室温", specification: "spec", type: "float", writable: false, targetArea: "R1" },
    ]);
    renderPage();

    expect(screen.getByText("読み込み中…")).toBeInTheDocument();

    const row = await screen.findByTestId("device-point-row");
    const link = within(row).getByRole("link", { name: "室温" });
    expect(link.tagName).toBe("A");
    expect(link).toHaveAttribute("href", "/points/point%3A1");
    expect(screen.getByText("AHU-1")).toBeInTheDocument();
  });

  it("shows an inline error banner when the fetch fails", async () => {
    deviceGet.mockRejectedValueOnce(new Error("boom"));
    pointsGet.mockRejectedValueOnce(new Error("boom"));
    renderPage();

    expect(await screen.findByTestId("inline-banner-error")).toHaveTextContent(
      "デバイス情報の取得に失敗しました。",
    );
  });
});
