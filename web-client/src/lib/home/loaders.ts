import { DEFAULT_HEALTH_QUERY } from "@/lib/health/query";
import type { HealthSummary } from "@/lib/health/repository";
import { fetchPointHealthSummary } from "@/lib/health/repository";
import type { OperationsSummary } from "@/lib/operations/repository";
import { fetchOperationsSummary } from "@/lib/operations/repository";
import {
  listBuildings,
  listDevices,
  listFloorDevices,
  listFloors,
  listPoints,
  listSpaces,
} from "@/lib/resources/repository";
import type { ResourceRef } from "@/lib/resources/types";
import type { PointAlarm } from "@/lib/telemetry/alarm";
import { loadPointsAlarms } from "@/lib/telemetry/alarm-loader";
import type { PointFreshness } from "@/lib/telemetry/freshness";
import { loadPointsFreshness } from "@/lib/telemetry/freshness-loader";
import { getTelemetryConfig } from "@/lib/telemetry/repository";
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
  /**
   * Per-point value-threshold alarms (#158 Phase 2a). Takes the {@link NamedPoint}s so each point's
   * opt-in thresholds are applied; points without thresholds come back `unknown` (not surfaced).
   */
  loadAlarms: (points: NamedPoint[]) => Promise<PointAlarm[]>;
  /**
   * Platform 由来のデータ流量 KPI（#451 Phase 1）。建物/フロアのスコープを持たない
   * （Point 母数側とは別軸 — `@/lib/health/repository` の責務）ので、フロア選択とは独立に読める。
   */
  loadOperationsSummary: () => Promise<OperationsSummary>;
  /**
   * 登録 Point 数 / Fresh 率の母数（#451 Phase 1 残作業）。判定の正本である `GET
   * /api/telemetry/health/summary`（#452）をそのまま使う — クライアント側で `loadFreshness` の
   * 結果を再集計しない（`summarizeFreshness` は #452 以前の暫定実装だった）。`floorDtId` を省略する
   * と「すべてのフロア」選択時と同じ、建物全体の集計をサーバ側 1 リクエストで返す。
   */
  loadHealthSummary: (
    buildingDtId: string,
    floorDtId?: string,
  ) => Promise<HealthSummary>;
};

type FloorTraversal = {
  listSpaces: (floorDtId: string) => Promise<ResourceRef[]>;
  listDevices: (spaceDtId: string) => Promise<ResourceRef[]>;
  listFloorDevices: (floorDtId: string) => Promise<ResourceRef[]>;
  listPoints: (deviceDtId: string) => Promise<
    {
      id: string;
      name: string;
      expectedIntervalSeconds?: number | null;
      alarmHigh?: number | null;
      alarmLow?: number | null;
      warnHigh?: number | null;
      warnLow?: number | null;
    }[]
  >;
};

/**
 * Every point on a floor: those of devices in its rooms (room → device → point), then those of devices
 * placed directly on the floor (#544), which have no room name. Carries the owning room + device names
 * so the attention list can say where a point lives without a second lookup. A failing room, device or
 * floor-device read degrades to an empty list rather than sinking the floor.
 */
export async function collectFloorPoints(
  floorDtId: string,
  deps: FloorTraversal,
): Promise<NamedPoint[]> {
  const pointsOf = async (d: ResourceRef, spaceName?: string) => {
    const points = await deps.listPoints(d.dtId).catch(() => []);
    return points.map((p) => ({
      pointId: p.id,
      name: p.name,
      deviceName: d.name,
      spaceName,
      expectedIntervalSeconds: p.expectedIntervalSeconds,
      thresholds: {
        alarmHigh: p.alarmHigh,
        alarmLow: p.alarmLow,
        warnHigh: p.warnHigh,
        warnLow: p.warnLow,
      },
    }));
  };

  const [spaces, floorDevices] = await Promise.all([
    deps.listSpaces(floorDtId),
    deps.listFloorDevices(floorDtId).catch(() => [] as ResourceRef[]),
  ]);
  const inRooms = await Promise.all(
    spaces.map(async (s) => {
      const devices = await deps
        .listDevices(s.dtId)
        .catch(() => [] as ResourceRef[]);
      return (
        await Promise.all(devices.map((d) => pointsOf(d, s.name)))
      ).flat();
    }),
  );
  const onFloor = await Promise.all(floorDevices.map((d) => pointsOf(d)));
  // A point appears once per floor even if its device is reachable both ways (defence in depth: the
  // server already leaves a same-floor room-placed device out of the floor list).
  const seen = new Set<string>();
  return [...inRooms.flat(), ...onFloor.flat()].filter(
    (p) => !seen.has(p.pointId) && (seen.add(p.pointId), true),
  );
}

/**
 * Production wiring over the resource/telemetry façades. Freshness is scoped to one floor's points at
 * a time by the caller, keeping the unbounded N+1 fan-out in {@link loadPointsFreshness} to a sensible
 * batch. A single space/device fetch failing degrades to an empty list rather than sinking the floor.
 */
export const productionHomeLoaders: HomeLoaders = {
  loadBuildings: () => listBuildings(),
  loadFloors: (buildingDtId) => listFloors(buildingDtId),
  loadFloorPoints: (floorDtId) =>
    collectFloorPoints(floorDtId, {
      listSpaces,
      listDevices,
      listFloorDevices,
      listPoints,
    }),
  loadFreshness: async (points) => {
    // Live thresholds (system default + admin override) from the all-role read surface (#183); the
    // fetch is cached, so this per-floor call does not refetch. Falls back to the defaults on failure.
    const cfg = await getTelemetryConfig();
    return loadPointsFreshness(
      points.map((p) => p.pointId),
      {
        now: new Date(),
        thresholdSeconds: cfg.staleThresholdSeconds,
        intervalMultiplier: cfg.staleIntervalMultiplier,
        expectedIntervalSeconds: new Map(
          points.map((p) => [p.pointId, p.expectedIntervalSeconds]),
        ),
      },
    );
  },
  // #158 Phase 2a: value-threshold alarms. A separate batch-latest read from loadFreshness (the
  // operator screen is admin-only and low-frequency); consolidating the two reads into one is a
  // possible follow-up.
  loadAlarms: async (points) =>
    loadPointsAlarms(
      points.map((p) => p.pointId),
      new Map(points.map((p) => [p.pointId, p.thresholds])),
    ),
  loadOperationsSummary: () => fetchOperationsSummary(),
  loadHealthSummary: (buildingDtId, floorDtId) =>
    fetchPointHealthSummary({
      ...DEFAULT_HEALTH_QUERY,
      buildingDtId,
      floorDtId,
    }),
};
