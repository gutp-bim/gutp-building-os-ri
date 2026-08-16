import type { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { PointInfo } from "./point-info";

// THX で実際に観測された形: twin にメタデータはあるのに、API が返さないので UI には届かない。
// このフィクスチャが手で `buildingName` / `type` / `specification` を与えていたため、
// バックエンドが null を返している事実をフロントのテストは一切捕捉できていなかった (#294)。
const unresolvedPointDetail = {
  point: {
    id: "172_31_105_17-3002",
    name: "On/Off Status",
    // GetPoint が SELECT していなかった項目 — 修正前はここが常に undefined だった
    type: undefined,
    specification: undefined,
    writable: undefined,
    // BACnet アドレッシングはあるが deviceIdBacnet は無い。旧 getControlType は
    // deviceIdBacnet だけを見ていたので、この点は「BACnet ではない」と判定されていた
    objectTypeBacnet: "3",
    instanceNoBacnet: 0,
  },
  device: { buildingName: undefined },
  floor: undefined,
  space: undefined,
} as unknown as PointDetail;

describe("PointInfo missing metadata (#294)", () => {
  it("labels an unresolved hierarchy as 未割当 rather than -", () => {
    render(<PointInfo pointDetail={unresolvedPointDetail} />);

    // "-" は「値が無い」と「表示が壊れている」の区別がつかない。運用者が次に何をすべきか
    // （階層を割り当てる）が分かる言葉にする。ビル / フロア / スペースの 3 箇所。
    expect(screen.getAllByText("未割当").length).toBeGreaterThanOrEqual(3);
  });

  it("does not render unknown writability as 読み取り専用", () => {
    render(<PointInfo pointDetail={unresolvedPointDetail} />);
    expect(screen.queryByText("不可（読み取り専用）")).not.toBeInTheDocument();
  });

  it("renders writable=false as 読み取り専用", () => {
    render(
      <PointInfo
        pointDetail={
          {
            ...unresolvedPointDetail,
            point: { ...unresolvedPointDetail.point, writable: false },
          } as unknown as PointDetail
        }
      />,
    );
    expect(screen.getByText("不可（読み取り専用）")).toBeInTheDocument();
  });

  it("reports BACnet collection from object addressing alone", () => {
    render(<PointInfo pointDetail={unresolvedPointDetail} />);

    // instanceNoBacnet は 0 — truthy 判定だと落ちる値。deviceIdBacnet は無い。
    expect(screen.getByText("収集プロトコル")).toBeInTheDocument();
    expect(screen.getByText("BACnet")).toBeInTheDocument();
    expect(screen.getByText("BACnet 情報")).toBeInTheDocument();
  });

  it("shows point type and specification once the API returns them", () => {
    render(
      <PointInfo
        pointDetail={
          {
            ...unresolvedPointDetail,
            point: {
              ...unresolvedPointDetail.point,
              type: "On_Off_Status",
              specification: "Status",
            },
          } as unknown as PointDetail
        }
      />,
    );

    expect(screen.getByText("On_Off_Status")).toBeInTheDocument();
    expect(screen.getByText("Status")).toBeInTheDocument();
  });

  it("shows MQTT protocol, concrete topic, unit, and interval from point metadata", () => {
    render(
      <PointInfo
        pointDetail={
          {
            ...unresolvedPointDetail,
            point: {
              ...unresolvedPointDetail.point,
              protocol: "mqtt",
              localId: "takenaka.co.jp/Tokyo/THX/HVAC/temp/R",
              unit: "Cel",
              interval: 300,
            },
          } as unknown as PointDetail
        }
      />,
    );

    expect(screen.getByText("MQTT")).toBeInTheDocument();
    expect(
      screen.getByText("takenaka.co.jp/Tokyo/THX/HVAC/temp/R"),
    ).toBeInTheDocument();
    expect(screen.getByText("Cel")).toBeInTheDocument();
    expect(screen.getByText("300 s")).toBeInTheDocument();
  });
});
