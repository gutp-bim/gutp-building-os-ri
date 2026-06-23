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

export function toPointResource(p: Point): PointResource {
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
