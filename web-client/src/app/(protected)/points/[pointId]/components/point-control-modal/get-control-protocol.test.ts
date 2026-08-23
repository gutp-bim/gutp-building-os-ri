import { aPointDetail } from "@/lib/resources/test-fixtures";
import type { PointResource } from "@/lib/resources/types";
import { describe, expect, it } from "vitest";
import { getControlProtocol } from "./get-control-protocol";

// Typed base, no cast: the previous `as PointDetailResource` over a partial literal meant tsc said
// nothing if this predicate later consulted a field the fixture omits.
const detail = (point: Partial<PointResource>) => aPointDetail({ point });

describe("getControlProtocol", () => {
  it("returns BACnet when objectTypeBacnet is set", () => {
    expect(getControlProtocol(detail({ objectTypeBacnet: "AV" }))).toBe(
      "BACnet",
    );
  });

  it("returns BACnet when instanceNoBacnet is 0 (a valid BACnet instance number)", () => {
    expect(getControlProtocol(detail({ instanceNoBacnet: 0 }))).toBe("BACnet");
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
