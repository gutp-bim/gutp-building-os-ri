import { describe, expect, it, vi } from "vitest";

const { getMock, devicesList, spacesList } = vi.hoisted(() => ({
  getMock: vi.fn(),
  devicesList: vi.fn(),
  spacesList: vi.fn(),
}));
vi.mock("@/lib/infra/aspida-client", () => ({
  apiClient: () => ({
    api: {
      v1: {
        devices: { $get: devicesList, _deviceDtId: () => ({ $get: getMock }) },
        floors: { _floorDtId: () => ({ $get: getMock }) },
        spaces: { $get: spacesList, _spaceDtId: () => ({ $get: getMock }) },
      },
    },
  }),
}));

import {
  getDevice,
  getFloorRef,
  getSpaceRef,
  listChildren,
  listFloorDevices,
} from "./repository";

describe("single-resource reads (#350 4c)", () => {
  it("getDevice returns the domain device with its attributes", async () => {
    getMock.mockResolvedValue({
      dtId: "urn:dev:1",
      id: "DEV1",
      name: "AHU-1",
      deviceType: "ahu",
      supplier: "ACME",
      gatewayId: "gw1",
    });
    await expect(getDevice("urn:dev:1")).resolves.toMatchObject({
      type: "device",
      id: "DEV1",
      deviceType: "ahu",
      supplier: "ACME",
      gatewayId: "gw1",
      owner: null,
    });
  });

  it("getFloorRef and getSpaceRef return a plain ref", async () => {
    getMock.mockResolvedValue({ dtId: "urn:fl:1", id: "F1", name: "1F" });
    await expect(getFloorRef("urn:fl:1")).resolves.toEqual({
      type: "floor",
      dtId: "urn:fl:1",
      id: "F1",
      name: "1F",
    });
    await expect(getSpaceRef("urn:fl:1")).resolves.toMatchObject({
      type: "space",
    });
  });
});

describe("devices placed directly on a floor (#544)", () => {
  it("listFloorDevices asks for the floor's own devices", async () => {
    devicesList.mockResolvedValue([
      { dtId: "urn:dev:m", id: "METER", name: "電力量計" },
    ]);

    const devices = await listFloorDevices("urn:fl:1");

    expect(devicesList).toHaveBeenCalledWith({
      query: { floorDtId: "urn:fl:1" },
    });
    expect(devices).toMatchObject([{ type: "device", id: "METER" }]);
  });

  it("a floor's children are its rooms followed by the devices placed directly on it", async () => {
    spacesList.mockResolvedValue([{ dtId: "urn:sp:1", id: "R1", name: "101" }]);
    devicesList.mockResolvedValue([
      { dtId: "urn:dev:m", id: "METER", name: "電力量計" },
    ]);

    const children = await listChildren({
      type: "floor",
      dtId: "urn:fl:1",
      id: "F1",
      name: "1F",
    });

    expect(children.map((c) => [c.type, c.id])).toEqual([
      ["space", "R1"],
      ["device", "METER"],
    ]);
  });
});
