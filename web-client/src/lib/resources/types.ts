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
  labels: string | null;
  specification: string | null;
  /** The point's measurement kind (aspida `Point.type`), renamed to avoid clashing with `type`. */
  kind: string | null;
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
