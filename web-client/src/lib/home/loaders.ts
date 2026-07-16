import {
  listBuildings,
  listDevices,
  listFloors,
  listPoints,
  listSpaces,
} from "@/lib/resources/repository";
import type { ResourceRef } from "@/lib/resources/types";
import { DEFAULT_STALE_THRESHOLD_SECONDS, type PointFreshness } from "@/lib/telemetry/freshness";
import { loadPointsFreshness } from "@/lib/telemetry/freshness-loader";
import type { NamedPoint } from "./aggregate";

/**
 * The data-access seam for the operator home (#158). Everything the view needs is expressed as four
 * injectable async loaders so the component is unit-testable offline (fakes in tests, the production
 * impl below in the app). This keeps the N+1 twin traversal and freshness fan-out out of the render
 * layer and in one swappable place.
 */
export type HomeLoaders = {
  loadBuildings: () => Promise<ResourceRef[]>;
  loadFloors: (buildingDtId: string) => Promise<ResourceRef[]>;
  /** All points under a floor (space → device → point traversal), as id+name pairs. */
  loadFloorPoints: (floorDtId: string) => Promise<NamedPoint[]>;
  /** Per-point freshness for the given point ids (fans out latest-sample fetches). */
  loadFreshness: (pointIds: string[]) => Promise<PointFreshness[]>;
};

/**
 * Production wiring over the resource/telemetry façades. Freshness is scoped to one floor's points at
 * a time by the caller, keeping the unbounded N+1 fan-out in {@link loadPointsFreshness} to a sensible
 * batch. A single space/device fetch failing degrades to an empty list rather than sinking the floor.
 */
export const productionHomeLoaders: HomeLoaders = {
  loadBuildings: () => listBuildings(),
  loadFloors: (buildingDtId) => listFloors(buildingDtId),
  loadFloorPoints: async (floorDtId) => {
    const spaces = await listSpaces(floorDtId);
    const deviceLists = await Promise.all(
      spaces.map((s) => listDevices(s.dtId).catch(() => [] as ResourceRef[])),
    );
    const devices = deviceLists.flat();
    const pointLists = await Promise.all(
      devices.map((d) => listPoints(d.dtId).catch(() => [])),
    );
    return pointLists.flat().map((p) => ({ pointId: p.id, name: p.name }));
  },
  loadFreshness: (pointIds) =>
    loadPointsFreshness(pointIds, {
      now: new Date(),
      thresholdSeconds: DEFAULT_STALE_THRESHOLD_SECONDS,
    }),
};
