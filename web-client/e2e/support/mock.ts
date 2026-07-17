import type { Page, Route } from "@playwright/test";

/** Fulfill a route with a JSON body. */
export function fulfillJson(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({
    status,
    contentType: "application/json",
    body: JSON.stringify(body),
  });
}

/** ISO timestamp `secondsAgo` before now — for telemetry freshness fixtures. */
export function isoSecondsAgo(secondsAgo: number): string {
  return new Date(Date.now() - secondsAgo * 1000).toISOString();
}

// Shared twin fixtures (shape = aspida Building/Floor `{ dtId, id, name }`).
export const BUILDING = { dtId: "bldg-e2e", id: "bldg-e2e", name: "E2E Demo Building" };
export const FLOOR = { dtId: "floor-1", id: "floor-1", name: "1F オフィス" };

/** Stub the resource tree's root load (`GET /buildings`). */
export async function mockBuildings(page: Page, buildings = [BUILDING]): Promise<void> {
  await page.route("**/buildings*", (route) => fulfillJson(route, buildings));
}

/** Stub the building→floor expansion (`GET /floors?buildingDtId=...`). */
export async function mockFloors(page: Page, floors = [FLOOR]): Promise<void> {
  await page.route("**/floors*", (route) => fulfillJson(route, floors));
}

// Deeper twin fixtures for floor→point traversal (space → device → point).
export const SPACE = { dtId: "space-1", id: "space-1", name: "執務室" };
export const DEVICE = { dtId: "dev-1", id: "dev-1", name: "AHU-1" };

/** Stub the floor→space expansion (`GET /spaces?floorDtId=...`). */
export async function mockSpaces(page: Page, spaces = [SPACE]): Promise<void> {
  await page.route("**/spaces*", (route) => fulfillJson(route, spaces));
}

/** Stub the space→device expansion (`GET /devices?spaceDtId=...`). */
export async function mockDevices(page: Page, devices = [DEVICE]): Promise<void> {
  await page.route("**/devices*", (route) => fulfillJson(route, devices));
}

/** Stub the device→point expansion (`GET /points?deviceDtId=...`). */
export async function mockPoints(
  page: Page,
  points: { dtId: string; id: string; name: string }[],
): Promise<void> {
  await page.route("**/points*", (route) => fulfillJson(route, points));
}

/**
 * Stub the freshness batch endpoint `POST /telemetries/query/batch-latest` (#182). `latestByPoint`
 * maps a pointId to the ISO timestamp of its most recent sample; a pointId absent from the map is
 * returned with `datetime: null` (→ classified "missing"). The handler echoes back exactly the ids
 * the client posted, so it also exercises the client-side chunking of over-cap floors.
 */
export async function mockLatestTelemetry(
  page: Page,
  latestByPoint: Record<string, string>,
): Promise<void> {
  await page.route("**/telemetries/query/batch-latest", (route) => {
    const { pointIds = [] } = (route.request().postDataJSON() ?? {}) as {
      pointIds?: string[];
    };
    const rows = pointIds.map((pointId) => {
      const datetime = latestByPoint[pointId] ?? null;
      return { pointId, datetime, value: datetime ? 1 : null };
    });
    return fulfillJson(route, rows);
  });
}

/**
 * Stub the freshness batch endpoint to fail, so specs can assert the operator home shows an error
 * banner instead of silently classifying every point as 欠測 (missing) — #182 review point 1.
 */
export async function mockLatestTelemetryFailure(
  page: Page,
  status = 503,
): Promise<void> {
  await page.route("**/telemetries/query/batch-latest", (route) =>
    fulfillJson(route, { error: "unavailable" }, status),
  );
}
