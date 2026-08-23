/**
 * Domain types for the resource explorer (#UI-improve). These are intentionally decoupled from the
 * aspida-generated `@types` so that an API/Swagger change is absorbed in `mapping.ts` + `repository.ts`
 * only — UI code imports from here, never from `aspida-client/generated`.
 */

export type ResourceType = "building" | "floor" | "space" | "device" | "point";

/** Minimal reference to any resource node, enough to render a tree row and route to its detail. */
export type ResourceRef = {
  type: ResourceType;
  /** Digital-twin id (node URI). For points this differs from {@link ResourceRef.id}. */
  dtId: string;
  /** Business id. For points this is the pointId used by telemetry/control/authorization. */
  id: string;
  name: string;
};

/** A point with the extra fields the detail pane and telemetry view need. Nulls are normalized. */
export type PointResource = ResourceRef & {
  type: "point";
  writable: boolean | null;
  unit: string | null;
  scale: number | null;
  specification: string | null;
  /**
   * The point's measurement kind (aspida `Point.type`), renamed because `type` is already the
   * resource discriminator on {@link ResourceRef}.
   *
   * **Trap when migrating a screen off the wire type (#350 4b/4c):** `point.type` still compiles
   * against `PointResource` — it just silently becomes the literal `"point"` for every point instead
   * of the measurement kind. TypeScript cannot catch it (both are non-empty strings), so a row like
   * 「ポイント種別」 would quietly render "point" everywhere. That is the #294/#298 failure class this
   * façade exists to prevent. Grep every `\.point\.type` when swapping a prop to
   * {@link PointDetailResource} and rewrite it to `.point.kind`.
   */
  kind: string | null;
  /**
   * Expected telemetry interval in seconds (aspida `Point.interval`, sbco:interval). Drives per-point
   * stale detection (#183); null when the twin has no expected interval for this point.
   */
  expectedIntervalSeconds: number | null;
  /**
   * Opt-in per-point alarm thresholds (#158 Phase 2a, `bos:alarmHigh`/`alarmLow`/`warnHigh`/`warnLow`);
   * null when unset. `alarm*` = critical (outer) limits, `warn*` = inner limits. Drive
   * {@link classifyPointAlarm}. Distinct from control-write bounds (ADR-0005).
   */
  alarmHigh: number | null;
  alarmLow: number | null;
  warnHigh: number | null;
  warnLow: number | null;
  /**
   * BACnet native addressing (`sbco:objectTypeBacnet` / `instanceNoBacnet` / `deviceIdBacnet`). This
   * is *addressing*, not writability — whether a point can be controlled is decided by
   * {@link PointResource.writable} and the control schema (#294). `instanceNoBacnet` of 0 is a valid
   * instance, so consumers must test `!= null`, never truthiness.
   */
  objectTypeBacnet: string | null;
  instanceNoBacnet: number | null;
  deviceIdBacnet: string | null;
  /**
   * The BACnet raw span (`sbco:minPresValue`/`maxPresValue`). **Display-only fallback** — the
   * authority for a control write range is the ControlSchema's `bos:minValue`/`maxValue`
   * (ADR-0005 §2.5, `resolve-control-range.ts`).
   */
  minPresValue: number | null;
  maxPresValue: number | null;
  /** `sbco:targetArea` — the area the point measures/controls; rendered by the device point table. */
  targetArea: string | null;
};

/**
 * The control schema (`bos:*`) — the **authority** for a control write range (ADR-0005 §2.5).
 * Distinct from {@link PointResource.minPresValue}/`maxPresValue`, which are the BACnet raw span and
 * a display-only fallback.
 */
export type ControlSchemaResource = {
  dataType: string | null;
  enumLabels: string | null;
  minValue: number | null;
  maxValue: number | null;
};

/** A device, with the attributes the detail panes render. Nulls are normalized. */
export type DeviceResource = ResourceRef & {
  type: "device";
  deviceType: string | null;
  supplier: string | null;
  owner: string | null;
  site: string | null;
  buildingName: string | null;
  gatewayId: string | null;
};

/**
 * Everything the point detail screen needs, as domain types — the façade equivalent of the aspida
 * `PointDetail` (#350). Spatial context is optional because the twin may not place a point.
 */
export type PointDetailResource = {
  point: PointResource;
  device: DeviceResource | null;
  floor: ResourceRef | null;
  space: ResourceRef | null;
  controlSchema: ControlSchemaResource | null;
};

/** One cross-resource search match. */
export type SearchHit = {
  type: ResourceType;
  dtId: string;
  id: string;
  name: string;
  /** Owning building's dtId when known (building-scoped search), else null. */
  buildingDtId: string | null;
};

export type SearchParams = {
  q?: string;
  type?: ResourceType;
  buildingId?: string;
  /** SBCO customTags keys; matches customTags[key] == true. Multiple = AND (#332). */
  tags?: string[];
  limit?: number;
  offset?: number;
};

/** sbco:identifiers (map<string, string>) and sbco:customTags (map<string, boolean>). */
export type ResourceMetadata = {
  identifiers: Record<string, string>;
  customTags: Record<string, boolean>;
};

export type ResourceMetadataPatch = {
  identifiers?: Record<string, string | null>;
  customTags?: Record<string, boolean | null>;
};
