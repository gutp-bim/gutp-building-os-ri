import type { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { PointInfo } from "./point-info";

const pointDetail = {
  point: { id: "p1", name: "室温", rowDataString: '{"a":1}', writable: false },
  device: { buildingName: "棟A" },
  floor: { name: "1F" },
  space: { name: "会議室" },
} as unknown as PointDetail;

function openJsonModalAndCopy() {
  render(<PointInfo pointDetail={pointDetail} />);
  fireEvent.click(screen.getByText("元データを表示"));
  fireEvent.click(screen.getByTitle("クリップボードにコピー"));
}

afterEach(() => vi.restoreAllMocks());

describe("PointInfo clipboard feedback (#196)", () => {
  it("confirms a successful copy and copies the raw data", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    openJsonModalAndCopy();

    expect(await screen.findByTestId("copy-success")).toHaveTextContent("コピーしました");
    expect(writeText).toHaveBeenCalledWith('{"a":1}');
  });

  it("surfaces a copy failure instead of failing silently", async () => {
    const writeText = vi.fn().mockRejectedValue(new Error("denied"));
    Object.assign(navigator, { clipboard: { writeText } });

    openJsonModalAndCopy();

    expect(await screen.findByTestId("copy-error")).toHaveTextContent("コピーに失敗しました");
  });
});
