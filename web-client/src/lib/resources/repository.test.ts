import { describe, expect, it, vi } from "vitest";

const { getMock } = vi.hoisted(() => ({ getMock: vi.fn() }));
vi.mock("@/lib/infra/aspida-client", () => ({
  apiClient: () => ({
    devices: { _deviceDtId: () => ({ $get: getMock }) },
    floors: { _floorDtId: () => ({ $get: getMock }) },
    spaces: { _spaceDtId: () => ({ $get: getMock }) },
  }),
}));

import { getDevice, getFloorRef, getSpaceRef } from "./repository";

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
