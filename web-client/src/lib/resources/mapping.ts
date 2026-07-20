import type {
  Point,
  ResourceSearchHit,
} from "@/lib/infra/aspida-client/generated/@types";
import type {
  PointResource,
  ResourceRef,
  ResourceType,
  SearchHit,
} from "./types";

/** Any aspida entity that carries the common dtId/id/name triple (Building/Floor/Space/Device). */
type DtEntity = { dtId: string; id: string; name: string };

export function toRef(type: ResourceType, e: DtEntity): ResourceRef {
  return { type, dtId: e.dtId, id: e.id, name: e.name };
}

/**
 * The API `Point` (entity) now also returns the #158 Phase 2a alarm thresholds, but the aspida `Point`
 * type won't declare them until `./sync-type.bash` is re-run against a live API server. Read them off a
 * narrowly-widened view until then (the fields genuinely flow from the backend); the regen is a no-op
 * that simply formalizes these into the generated type.
 */
type PointWithAlarms = Point & {
  alarmHigh?: number | null;
  alarmLow?: number | null;
  warnHigh?: number | null;
  warnLow?: number | null;
};

export function toPointResource(p: Point): PointResource {
  const pa = p as PointWithAlarms;
  return {
    type: "point",
    dtId: p.dtId,
    id: p.id,
    name: p.name,
    writable: p.writable ?? null,
    unit: p.unit ?? null,
    scale: p.scale ?? null,
    labels: p.labels ?? null,
    specification: p.specification ?? null,
    kind: p.type ?? null,
    expectedIntervalSeconds: p.interval ?? null,
    alarmHigh: pa.alarmHigh ?? null,
    alarmLow: pa.alarmLow ?? null,
    warnHigh: pa.warnHigh ?? null,
    warnLow: pa.warnLow ?? null,
  };
}

export function toSearchHit(h: ResourceSearchHit): SearchHit {
  return {
    type: h.type as ResourceType,
    dtId: h.dtId,
    id: h.id,
    name: h.name,
    buildingDtId: h.buildingDtId ?? null,
  };
}
