import { aPointDetail } from "@/lib/resources/test-fixtures";
import type { PointResource } from "@/lib/resources/types";
import { describe, expect, it } from "vitest";
import {
  getControlModalKind,
  getControlProtocol,
} from "./get-control-protocol";

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

describe("getControlModalKind (#638)", () => {
  const bacnet = (point: Partial<PointResource>, dataType: string | null) =>
    aPointDetail({
      point: { deviceIdBacnet: "dev1", ...point },
      controlSchema: dataType === null ? null : { dataType },
    });

  it("opens the modal for the schema's type, spelled as the server reads it", () => {
    expect(getControlModalKind(bacnet({ writable: true }, "number"))).toBe(
      "number",
    );
    expect(getControlModalKind(bacnet({ writable: true }, " Boolean "))).toBe(
      "boolean",
    );
    expect(getControlModalKind(bacnet({ writable: null }, "ENUM"))).toBe(
      "enum",
    );
  });

  it("offers nothing the server would refuse or no modal could show", () => {
    expect(
      getControlModalKind(bacnet({ writable: false }, "number")),
    ).toBeNull();
    expect(
      getControlModalKind(bacnet({ writable: true }, "string")),
    ).toBeNull();
    expect(getControlModalKind(bacnet({ writable: true }, null))).toBeNull();
    expect(
      getControlModalKind(aPointDetail({ point: { writable: true } })),
    ).toBeNull();
  });
});
