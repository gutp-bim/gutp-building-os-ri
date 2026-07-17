import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// floors/[floorId] and spaces/[spaceId] share this exact loading→cards→error→empty structure;
// this suite exercises the shared pattern once against the buildings page.
const floorsGet = vi.fn();
vi.mock("@/lib/infra/aspida-client", () => ({
  apiClient: () => ({ floors: { $get: floorsGet } }),
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

import BuildingDetailPageComponent from "./page-component";

beforeEach(() => {
  floorsGet.mockReset();
});

describe("BuildingDetailPageComponent (#195)", () => {
  it("shows a text loading state then keyboard-accessible floor cards", async () => {
    floorsGet.mockResolvedValueOnce([{ id: "floor:1", dtId: "urn:floor:1", name: "1F" }]);
    render(<BuildingDetailPageComponent buildingId="urn:bldg:1" />);

    expect(screen.getByText("読み込み中…")).toBeInTheDocument();

    const card = await screen.findByTestId("floor-card");
    expect(card.tagName).toBe("A");
    expect(card).toHaveAttribute("href", "/floors/urn%3Afloor%3A1");
    expect(card).toHaveAccessibleName("1F");
  });

  it("shows an inline error banner on fetch failure", async () => {
    floorsGet.mockRejectedValueOnce(new Error("boom"));
    render(<BuildingDetailPageComponent buildingId="urn:bldg:1" />);

    expect(await screen.findByTestId("inline-banner-error")).toHaveTextContent(
      "フロア情報の取得に失敗しました。",
    );
  });

  it("shows an empty state when there are no floors", async () => {
    floorsGet.mockResolvedValueOnce([]);
    render(<BuildingDetailPageComponent buildingId="urn:bldg:1" />);

    expect(await screen.findByText("フロアがありません。")).toBeInTheDocument();
  });
});
