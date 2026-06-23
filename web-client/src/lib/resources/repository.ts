import { apiClient } from "@/lib/infra/aspida-client";
import { toPointResource, toRef, toSearchHit } from "./mapping";
import { normalizeSearchParams } from "./search";
import type {
  PointResource,
  ResourceRef,
  SearchHit,
  SearchParams,
} from "./types";

/**
 * The single access façade for digital-twin resources. UI/components call these functions instead of
 * the aspida client directly, so an API change is absorbed here. Every function takes an optional
 * bearer `token` for server-side rendering (falls back to the cookie on the client).
 */

const enc = encodeURIComponent;

export async function listBuildings(token?: string): Promise<ResourceRef[]> {
  const res = await apiClient(token).buildings.$get();
  return res.map((b) => toRef("building", b));
}

export async function listFloors(
  buildingDtId: string,
  token?: string,
): Promise<ResourceRef[]> {
  const res = await apiClient(token).floors.$get({ query: { buildingDtId } });
  return res.map((f) => toRef("floor", f));
}

export async function listSpaces(
  floorDtId: string,
  token?: string,
): Promise<ResourceRef[]> {
  const res = await apiClient(token).spaces.$get({ query: { floorDtId } });
  return res.map((s) => toRef("space", s));
}

export async function listDevices(
  spaceDtId: string,
  token?: string,
): Promise<ResourceRef[]> {
  const res = await apiClient(token).devices.$get({ query: { spaceDtId } });
  return res.map((d) => toRef("device", d));
}

export async function listPoints(
  deviceDtId: string,
  token?: string,
): Promise<PointResource[]> {
  const res = await apiClient(token).points.$get({ query: { deviceDtId } });
  return res.map(toPointResource);
}

/** Lazy tree expansion: list the children one level below `parent`. Leaves (points) return []. */
export async function listChildren(
  parent: ResourceRef,
  token?: string,
): Promise<ResourceRef[]> {
  switch (parent.type) {
    case "building":
      return listFloors(parent.dtId, token);
    case "floor":
      return listSpaces(parent.dtId, token);
    case "space":
      return listDevices(parent.dtId, token);
    case "device":
      return listPoints(parent.dtId, token);
    case "point":
      return [];
  }
}

export async function getPointDetail(pointId: string, token?: string) {
  return apiClient(token).point_details._pointId(enc(pointId)).$get();
}

/**
 * Hydrate a single resource ref from its type + id (dtId for non-points, business id for points).
 * Used to restore the right pane from a `?sel=` deep link. Returns null when not found/forbidden.
 */
export async function resolveRef(
  type: ResourceRef["type"],
  idOrDtId: string,
  token?: string,
): Promise<ResourceRef | null> {
  const c = apiClient(token);
  try {
    switch (type) {
      case "building":
        return toRef(
          "building",
          await c.buildings._buildingDtId(enc(idOrDtId)).$get(),
        );
      case "floor":
        return toRef("floor", await c.floors._floorDtId(enc(idOrDtId)).$get());
      case "space":
        return toRef("space", await c.spaces._spaceDtId(enc(idOrDtId)).$get());
      case "device":
        return toRef(
          "device",
          await c.devices._deviceDtId(enc(idOrDtId)).$get(),
        );
      case "point": {
        const pd = await c.point_details._pointId(enc(idOrDtId)).$get();
        return toPointResource(pd.point);
      }
    }
  } catch {
    return null;
  }
}

export async function searchResources(
  params: SearchParams,
  token?: string,
): Promise<SearchHit[]> {
  const query = normalizeSearchParams(params);
  const res = await apiClient(token).resources.search.$get({ query });
  return res.map(toSearchHit);
}
