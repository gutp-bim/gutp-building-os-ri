/**
 * Typed fixture builders for the resources domain types.
 *
 * Test files used to hand-roll these — three near-identical `BASE_POINT` blocks across the point
 * detail slice — or, worse, cast a `Record<string, unknown>` to the domain type. A cast makes the
 * type checker silent about a misspelled or removed field, which is precisely the failure class the
 * façade exists to prevent (#294/#298). Overlaying a `Partial` on a typed base keeps tsc enforcing
 * the shape while a case still only writes the fields it cares about.
 *
 * Test-only, but it lives beside the types rather than under a slice so 4c/4d can use it too.
 */
import type {
  ControlSchemaResource,
  DeviceResource,
  PointDetailResource,
  PointResource,
  ResourceRef,
} from "./types";

export function aPoint(overrides: Partial<PointResource> = {}): PointResource {
  return {
    type: "point",
    dtId: "urn:pt:PT001",
    id: "PT001",
    name: "室温",
    kind: null,
    writable: null,
    unit: null,
    scale: null,
    specification: null,
    expectedIntervalSeconds: null,
    alarmHigh: null,
    alarmLow: null,
    warnHigh: null,
    warnLow: null,
    objectTypeBacnet: null,
    instanceNoBacnet: null,
    deviceIdBacnet: null,
    localId: null,
    protocol: null,
    minPresValue: null,
    maxPresValue: null,
    targetArea: null,
    ...overrides,
  };
}

export function aDevice(
  overrides: Partial<DeviceResource> = {},
): DeviceResource {
  return {
    type: "device",
    dtId: "urn:dev:DEV1",
    id: "DEV1",
    name: "AHU-1",
    deviceType: null,
    supplier: null,
    owner: null,
    site: null,
    buildingName: null,
    gatewayId: null,
    ...overrides,
  };
}

export function aControlSchema(
  overrides: Partial<ControlSchemaResource> = {},
): ControlSchemaResource {
  return {
    dataType: null,
    enumLabels: null,
    minValue: null,
    maxValue: null,
    ...overrides,
  };
}

/**
 * A point detail. `device`/`floor`/`space`/`controlSchema` default to `null` — the shape
 * `toPointDetail` produces for a point the twin does not place — so a case that cares about them
 * has to say so.
 */
export function aPointDetail(
  overrides: {
    point?: Partial<PointResource>;
    device?: DeviceResource | null;
    floor?: ResourceRef | null;
    space?: ResourceRef | null;
    controlSchema?: Partial<ControlSchemaResource> | null;
  } = {},
): PointDetailResource {
  return {
    point: aPoint(overrides.point),
    device: overrides.device ?? null,
    floor: overrides.floor ?? null,
    space: overrides.space ?? null,
    controlSchema:
      overrides.controlSchema === undefined || overrides.controlSchema === null
        ? null
        : aControlSchema(overrides.controlSchema),
  };
}
