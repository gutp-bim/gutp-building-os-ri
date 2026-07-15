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
