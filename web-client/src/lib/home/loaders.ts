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
import { DEFAULT_STALE_INTERVAL_MULTIPLIER } from "@/lib/telemetry/freshness-threshold";
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
  /**
   * Per-point freshness for the given points. Takes the {@link NamedPoint}s (not bare ids) so each
   * point's expected interval drives its own stale threshold (#183).
   */
  loadFreshness: (points: NamedPoint[]) => Promise<PointFreshness[]>;
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
    // Carry the owning space + device names down to each point so the attention list can show where
    // a stale/missing point lives (space → device) without a second lookup.
    const perSpace = await Promise.all(
      spaces.map(async (s) => {
        const devices = await listDevices(s.dtId).catch(() => [] as ResourceRef[]);
        const perDevice = await Promise.all(
          devices.map(async (d) => {
            const points = await listPoints(d.dtId).catch(() => []);
            return points.map((p) => ({
              pointId: p.id,
              name: p.name,
              deviceName: d.name,
              spaceName: s.name,
              expectedIntervalSeconds: p.expectedIntervalSeconds,
            }));
          }),
        );
        return perDevice.flat();
      }),
    );
    return perSpace.flat();
  },
  loadFreshness: (points) =>
    loadPointsFreshness(
      points.map((p) => p.pointId),
      {
        now: new Date(),
        // System-default fallback for points with no expected interval. The multiplier is a fixed
        // constant this slice (not an editable setting — that would be a false affordance until read
        // at runtime, #183); wiring a live all-role telemetry-threshold surface (both the default
        // threshold and the multiplier) stays a follow-up (#148/#176). The constants below match the
        // backend defaults.
        thresholdSeconds: DEFAULT_STALE_THRESHOLD_SECONDS,
        intervalMultiplier: DEFAULT_STALE_INTERVAL_MULTIPLIER,
        expectedIntervalSeconds: new Map(
          points.map((p) => [p.pointId, p.expectedIntervalSeconds]),
        ),
      },
    ),
};
