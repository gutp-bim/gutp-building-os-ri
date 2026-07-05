import { describe, expect, it } from "vitest";
import { getControlProtocol } from "./get-control-protocol";
import type { PointDetail } from "@/lib/infra/aspida-client/generated/@types";

const detail = (point: Partial<PointDetail["point"]>): PointDetail =>
  ({
    point: { dtId: "pt", id: "pt", name: "pt", ...point },
  }) as PointDetail;

describe("getControlProtocol", () => {
  it("returns BACnet when objectTypeBacnet is set", () => {
    expect(getControlProtocol(detail({ objectTypeBacnet: "AV" }))).toBe(
      "BACnet",
    );
  });

  it("returns BACnet when instanceNoBacnet is 0 (a valid BACnet instance number)", () => {
    expect(getControlProtocol(detail({ instanceNoBacnet: 0 }))).toBe(
      "BACnet",
    );
  });

  it("returns BACnet when deviceIdBacnet is set", () => {
    expect(getControlProtocol(detail({ deviceIdBacnet: "dev1" }))).toBe(
      "BACnet",
    );
  });

  it("returns null when no BACnet fields are present", () => {
    expect(getControlProtocol(detail({}))).toBeNull();
  });
});
