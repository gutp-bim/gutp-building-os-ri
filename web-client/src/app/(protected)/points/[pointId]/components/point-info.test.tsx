import type { PointDetailResource } from "@/lib/resources/types";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { PointInfo } from "./point-info";

// THX で実際に観測された形: twin にメタデータはあるのに、API が返さないので UI には届かない。
// このフィクスチャが手で `buildingName` / `kind` / `specification` を与えていたため、
// バックエンドが null を返している事実をフロントのテストは一切捕捉できていなかった (#294)。
//
// #350 4b: `as unknown as` キャストを外し素の domain 型にした。キャストがある間は
// フィールドの綴り違いや欠落を型チェッカーが握り潰していた。
const unresolvedPointDetail: PointDetailResource = {
  point: {
    type: "point",
    dtId: "urn:pt:172_31_105_17-3002",
    id: "172_31_105_17-3002",
    name: "On/Off Status",
    // GetPoint が SELECT していなかった項目 — 修正前はここが常に null だった
    kind: null,
    specification: null,
    writable: null,
    unit: null,
    scale: null,
    expectedIntervalSeconds: null,
    alarmHigh: null,
    alarmLow: null,
    warnHigh: null,
    warnLow: null,
    // BACnet アドレッシングはあるが deviceIdBacnet は無い。旧 getControlType は
    // deviceIdBacnet だけを見ていたので、この点は「BACnet ではない」と判定されていた
    objectTypeBacnet: "3",
    instanceNoBacnet: 0,
    deviceIdBacnet: null,
    minPresValue: null,
    maxPresValue: null,
  },
  device: {
    type: "device",
    dtId: "urn:dev:1",
    id: "DEV1",
    name: "",
    deviceType: null,
    supplier: null,
    owner: null,
    site: null,
    buildingName: null,
    gatewayId: null,
  },
  floor: null,
  space: null,
  controlSchema: null,
};

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
          } satisfies PointDetailResource
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
        pointDetail={{
          ...unresolvedPointDetail,
          point: {
            ...unresolvedPointDetail.point,
            // `kind`, not `type` — on the domain type `type` is the resource discriminator, so
            // setting it here would have silently tested nothing (#350 4b).
            kind: "On_Off_Status",
            specification: "Status",
          },
        }}
      />,
    );

    expect(screen.getByText("On_Off_Status")).toBeInTheDocument();
    expect(screen.getByText("Status")).toBeInTheDocument();
  });
});

describe("PointInfo field fidelity (#350 4b)", () => {
  // BACnet instance number 0 is legal. `|| "-"` renders it as "-", making an instance-0 point
  // indistinguishable from one the twin gives no instance number at all — and getCollectionProtocol
  // in the same screen deliberately tests `!= null` for exactly this reason.
  it("renders BACnet instance number 0 rather than treating it as missing", () => {
    render(<PointInfo pointDetail={unresolvedPointDetail} />);
    const row = screen
      .getByText("Instance No Bacnet")
      .closest("div")?.parentElement;
    expect(row?.textContent).toContain("0");
  });

  // `point.type` is the measurement kind on the wire, but the resource discriminator on the domain
  // type. A migration that leaves `.point.type` in place still compiles and silently renders the
  // literal "point" for every point (#294/#298 class).
  it("renders the measurement kind, not the resource discriminator", () => {
    render(<PointInfo pointDetail={unresolvedPointDetail} />);
    expect(screen.queryByText("point")).not.toBeInTheDocument();
  });
});
