import type {
  ControlSchema,
  Device,
  Point,
  PointDetail,
  ResourceSearchHit,
} from "@/lib/infra/aspida-client/generated/@types";
import type {
  ControlSchemaResource,
  DeviceResource,
  PointDetailResource,
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
    specification: p.specification ?? null,
    kind: p.type ?? null,
    expectedIntervalSeconds: p.interval ?? null,
    alarmHigh: p.alarmHigh ?? null,
    alarmLow: p.alarmLow ?? null,
    warnHigh: p.warnHigh ?? null,
    warnLow: p.warnLow ?? null,
    objectTypeBacnet: p.objectTypeBacnet ?? null,
    instanceNoBacnet: p.instanceNoBacnet ?? null,
    deviceIdBacnet: p.deviceIdBacnet ?? null,
    localId: p.localId ?? null,
    protocol: p.protocol ?? null,
    minPresValue: p.minPresValue ?? null,
    maxPresValue: p.maxPresValue ?? null,
    targetArea: p.targetArea ?? null,
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

/**
 * Projects a device onto the domain shape. The equipment attributes come from the twin's
 * `EquipmentExt` with a fallback to one of its `PointExt` rows (#294/#298) — that resolution happens
 * server-side; here they are simply normalized.
 */
export function toDeviceResource(d: Device): DeviceResource {
  return {
    type: "device",
    dtId: d.dtId,
    id: d.id,
    name: d.name,
    deviceType: d.deviceType ?? null,
    supplier: d.supplier ?? null,
    owner: d.owner ?? null,
    site: d.site ?? null,
    buildingName: d.buildingName ?? null,
    gatewayId: d.gatewayId ?? null,
  };
}

function toControlSchemaResource(c: ControlSchema): ControlSchemaResource {
  return {
    dataType: c.dataType ?? null,
    enumLabels: c.enumLabels ?? null,
    minValue: c.minValue ?? null,
    maxValue: c.maxValue ?? null,
  };
}

/**
 * Projects the wire `PointDetail` onto domain types (#350). Absent spatial context becomes `null`
 * rather than `undefined` so the UI has one shape to test.
 */
export function toPointDetail(d: PointDetail): PointDetailResource {
  return {
    point: toPointResource(d.point),
    device: d.device ? toDeviceResource(d.device) : null,
    floor: d.floor ? toRef("floor", d.floor) : null,
    space: d.space ? toRef("space", d.space) : null,
    controlSchema: d.controlSchema
      ? toControlSchemaResource(d.controlSchema)
      : null,
  };
}
