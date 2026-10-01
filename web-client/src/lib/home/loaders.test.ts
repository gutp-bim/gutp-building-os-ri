import type { DeviceResource, ResourceRef } from "@/lib/resources/types";
import { describe, expect, it, vi } from "vitest";
import { collectFloorPoints } from "./loaders";

const ref = (
  type: ResourceRef["type"],
  id: string,
  name = id,
): ResourceRef => ({
  type,
  dtId: `urn:${id}`,
  id,
  name,
});
const device = (id: string, name = id) =>
  ref("device", id, name) as DeviceResource;
const point = (id: string) => ({
  ...ref("point", id),
  expectedIntervalSeconds: 60,
  alarmHigh: null,
  alarmLow: null,
  warnHigh: null,
  warnLow: null,
});

describe("collectFloorPoints (#544)", () => {
  it("includes points of devices placed directly on the floor, without a room name", async () => {
    const deps = {
      listSpaces: vi.fn().mockResolvedValue([ref("space", "R1", "101")]),
      listDevices: vi.fn().mockResolvedValue([device("VAV", "VAV-1")]),
      listFloorDevices: vi.fn().mockResolvedValue([device("AHU", "空調機")]),
      listPoints: vi.fn(async (deviceDtId: string) =>
        deviceDtId === "urn:VAV" ? [point("P-VAV")] : [point("P-AHU")],
      ),
    };

    const points = await collectFloorPoints("urn:F1", deps);

    expect(deps.listFloorDevices).toHaveBeenCalledWith("urn:F1");
    expect(points.map((p) => [p.pointId, p.spaceName, p.deviceName])).toEqual([
      ["P-VAV", "101", "VAV-1"],
      ["P-AHU", undefined, "空調機"],
    ]);
  });

  it("a failing floor-device read degrades to the room-placed points", async () => {
    const deps = {
      listSpaces: vi.fn().mockResolvedValue([ref("space", "R1")]),
      listDevices: vi.fn().mockResolvedValue([device("VAV")]),
      listFloorDevices: vi.fn().mockRejectedValue(new Error("403")),
      listPoints: vi.fn().mockResolvedValue([point("P-VAV")]),
    };

    expect(
      (await collectFloorPoints("urn:F1", deps)).map((p) => p.pointId),
    ).toEqual(["P-VAV"]);
  });
});
