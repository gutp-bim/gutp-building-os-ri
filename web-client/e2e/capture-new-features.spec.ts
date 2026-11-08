import { create, toBinary } from "@bufbuild/protobuf";
import { test, type Page } from "@playwright/test";
import {
  ControlResult,
  ControlResultEventSchema,
} from "../src/lib/gen/point_control_pb";
import { LatestValueEventSchema } from "../src/lib/gen/telemetry_notification_pb";
import { loginAs } from "./support/auth";
import { fulfillJson, isoSecondsAgo } from "./support/mock";

// Screenshots of the features added in #635 / #636 / #637 for the README and the GitHub Pages site, from the
// real app driven against a route-mocked API (REST and gRPC-web) — no backend required. Run explicitly:
//   npx playwright test e2e/capture-new-features.spec.ts
// PNGs land in ../docs/screenshots/.
test.use({ viewport: { width: 1280, height: 860 } });

const SHOTS = "../docs/screenshots";

async function hideDevOverlay(page: Page): Promise<void> {
  await page.addStyleTag({
    content:
      "nextjs-portal, [data-nextjs-toast], #__next-build-watcher { display: none !important; }",
  });
}

/** One gRPC-web data frame (flag 0x00, 4-byte big-endian length, message). */
function dataFrame(message: Uint8Array): Buffer {
  const head = Buffer.alloc(5);
  head.writeUInt32BE(message.length, 1);
  return Buffer.concat([head, Buffer.from(message)]);
}

/** The gRPC-web trailer frame (flag 0x80) carrying grpc-status 0. */
function okTrailer(): Buffer {
  const text = Buffer.from("grpc-status: 0\r\ngrpc-message: \r\n");
  const head = Buffer.alloc(5);
  head[0] = 0x80;
  head.writeUInt32BE(text.length, 1);
  return Buffer.concat([head, text]);
}

/**
 * Answer gRPC-web calls from inside the page. Wrapping the browser's fetch (rather than a route fulfil) lets a
 * stream stay open: the latest-value stream (#636) yields its frames and then idles like a live connection —
 * a fulfilled route would close it at once and the page would read that as a dropped stream. A stream with
 * `close: true` ends with an OK trailer after its frames, as WaitForResult does.
 */
async function mockGrpc(
  page: Page,
  streams: Record<string, { framesBase64: string; close: boolean }>,
): Promise<void> {
  await page.addInitScript(
    (byPath: Record<string, { framesBase64: string; close: boolean }>) => {
      const original = window.fetch.bind(window);
      window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
        const url =
          typeof input === "string"
            ? input
            : input instanceof URL
              ? input.href
              : input.url;
        const path = Object.keys(byPath).find((p) => url.endsWith(p));
        if (!path) return original(input, init);
        const { framesBase64, close } = byPath[path];
        const bytes = Uint8Array.from(atob(framesBase64), (c) =>
          c.charCodeAt(0),
        );
        const body = new ReadableStream<Uint8Array>({
          start(controller) {
            if (bytes.length > 0) controller.enqueue(bytes);
            if (close) controller.close();
            else
              init?.signal?.addEventListener("abort", () =>
                controller.error(new DOMException("aborted", "AbortError")),
              );
          },
        });
        return Promise.resolve(
          new Response(body, {
            status: 200,
            headers: { "content-type": "application/grpc-web+proto" },
          }),
        );
      };
    },
    streams,
  );
}

const WATCH_LATEST = "TelemetryNotificationService/WatchLatest";
const WAIT_FOR_RESULT = "PointControlService/WaitForResult";

const POINT_ID = "SOS-PT-004";

async function mockSetpointPoint(
  page: Page,
  latest: { value: number; secondsAgo: number },
): Promise<void> {
  await page.route("**/point-details/**", (route) =>
    fulfillJson(route, {
      point: {
        dtId: POINT_ID,
        id: POINT_ID,
        name: "執務室 設定温度",
        unit: "℃",
        writable: true,
        gatewayId: "GW-SOS-001",
        objectTypeBacnet: "analogValue",
        instanceNoBacnet: 4,
        deviceIdBacnet: "1001",
      },
      building: { dtId: "bldg-sos", id: "bldg-sos", name: "SOS ビル" },
      floor: { dtId: "floor-3", id: "floor-3", name: "3F" },
      space: { dtId: "space-office", id: "space-office", name: "執務室" },
      device: {
        dtId: "dev-ahu-1",
        id: "dev-ahu-1",
        name: "AHU-1",
        buildingName: "SOS ビル",
      },
      controlSchema: {
        dataType: "number",
        minValue: 18,
        maxValue: 28,
        step: 0.5,
      },
    }),
  );
  await page.route("**/telemetries/query*", (route) => {
    const url = new URL(route.request().url());
    if (url.searchParams.get("latest") === "true")
      return fulfillJson(route, [
        { datetime: isoSecondsAgo(latest.secondsAgo), value: latest.value },
      ]);
    const now = Date.now();
    const series = Array.from({ length: 24 }, (_, i) => ({
      datetime: new Date(now - (23 - i) * 3600_000).toISOString(),
      // 26 ℃ until the 25 ℃ write an hour ago took effect (the matched row in 制御履歴).
      value: 23 - i <= 1 ? 25 : 26,
    }));
    return fulfillJson(route, series);
  });
}

test("capture: latest value pushed to the point page (#636)", async ({
  context,
  page,
}) => {
  await loginAs(context, "operator");
  await mockSetpointPoint(page, { value: 25, secondsAgo: 90 });
  await page.route("**/points/*/control-audit*", (route) =>
    fulfillJson(route, []),
  );
  const pushed = toBinary(
    LatestValueEventSchema,
    create(LatestValueEventSchema, {
      pointId: POINT_ID,
      value: { case: "numberValue", value: 24.5 },
      datetime: isoSecondsAgo(2),
      snapshot: false,
    }),
  );
  await mockGrpc(page, {
    [WATCH_LATEST]: {
      framesBase64: dataFrame(pushed).toString("base64"),
      close: false,
    },
  });
  await page.goto(`/points/${POINT_ID}`);
  await page.getByText("自動更新中").waitFor();
  await hideDevOverlay(page);
  await page.waitForTimeout(600);
  await page.screenshot({ path: `${SHOTS}/point-live-value.png` });
});

test("capture: control confirmed by the feedback point (#637)", async ({
  context,
  page,
}) => {
  await loginAs(context, "operator");
  await mockSetpointPoint(page, { value: 25, secondsAgo: 20 });
  const now = Date.now();
  await page.route("**/points/*/control-audit*", (route) =>
    fulfillJson(route, [
      {
        controlId: "c-2",
        pointId: POINT_ID,
        request: JSON.stringify({ value: 25 }),
        status: "success",
        createdAt: new Date(now - 3600_000).toISOString(),
        completedAt: new Date(now - 3599_000).toISOString(),
        actorSub: "operator",
        actorName: "運用担当",
        feedbackStatus: "matched",
        feedbackValue: "25",
        feedbackCompletedAt: new Date(now - 3570_000).toISOString(),
      },
      {
        controlId: "c-1",
        pointId: POINT_ID,
        request: JSON.stringify({ value: 24 }),
        status: "success",
        createdAt: new Date(now - 7200_000).toISOString(),
        completedAt: new Date(now - 7199_000).toISOString(),
        actorSub: "operator",
        actorName: "運用担当",
        feedbackStatus: "mismatch",
        feedbackValue: "26",
        feedbackCompletedAt: new Date(now - 7140_000).toISOString(),
      },
    ]),
  );
  await page.route("**/points/*/control", (route) =>
    route.request().method() === "POST"
      ? fulfillJson(route, { controlId: "c-3" }, 202)
      : route.fallback(),
  );
  const event = (result: ControlResult, feedbackValue = "") =>
    dataFrame(
      toBinary(
        ControlResultEventSchema,
        create(ControlResultEventSchema, {
          controlId: "c-3",
          result,
          response: "",
          timestamp: new Date().toISOString(),
          feedbackValue,
        }),
      ),
    );
  await mockGrpc(page, {
    [WATCH_LATEST]: { framesBase64: "", close: false },
    [WAIT_FOR_RESULT]: {
      framesBase64: Buffer.concat([
        event(ControlResult.SUCCESS),
        event(ControlResult.FEEDBACK_MATCHED, "24"),
        okTrailer(),
      ]).toString("base64"),
      close: true,
    },
  });

  await page.goto(`/points/${POINT_ID}`);
  await page.getByRole("button", { name: "制御信号を送信" }).click();
  await page.getByLabel(/^値/).fill("24");
  await page.getByRole("button", { name: "送信" }).click();
  await page.getByTestId("control-feedback-matched").waitFor();
  await hideDevOverlay(page);
  // Status bar (the two-stage result) + 制御履歴 (the 反映 column), side by side in one frame.
  await page.getByTestId("control-audit-row").first().waitFor();
  // The app shell scrolls its main pane, not the window, so a full-page capture stops at the viewport: make the
  // viewport tall enough for both parts instead, then clip to them.
  await page.setViewportSize({ width: 1280, height: 2400 });
  // The history chart sits between the two; leave it out so the result and 制御履歴 stay legible at page size.
  await page.getByTestId("warm-period-select").evaluate((el) => {
    let card: HTMLElement | null = el as HTMLElement;
    while (
      card?.parentElement &&
      !card.parentElement.querySelector('[data-testid="control-audit-history"]')
    )
      card = card.parentElement;
    if (card) card.style.display = "none";
  });
  await page
    .getByTestId("control-status-success")
    .evaluate((el) => el.closest("main")?.scrollTo(0, 0));
  const box = async (testId: string) => {
    const b = await page.getByTestId(testId).boundingBox();
    if (!b) throw new Error(`${testId} not rendered`);
    return b;
  };
  const bar = await box("control-status-success");
  const history = await box("control-audit-history");
  const top = Math.min(bar.y, history.y) - 16;
  const bottom = Math.max(bar.y + bar.height, history.y + history.height) + 16;
  await page.screenshot({
    path: `${SHOTS}/control-feedback-matched.png`,
    clip: { x: 224, y: top, width: 1280 - 224, height: bottom - top },
  });
});

test("capture: points the health list does not monitor (#635)", async ({
  context,
  page,
}) => {
  await loginAs(context, "operator");
  const item = (
    pointId: string,
    name: string,
    status: "Fresh" | "Stale" | "Missing" | "NotMonitored",
    extra: Record<string, unknown> = {},
  ) => ({
    pointId,
    pointDtId: pointId,
    name,
    unit: "℃",
    healthStatus: status,
    freshness: {
      status,
      lastSeen:
        status === "Missing" || status === "NotMonitored"
          ? null
          : isoSecondsAgo(status === "Stale" ? 1800 : 20),
      ageSeconds:
        status === "Missing" || status === "NotMonitored"
          ? null
          : status === "Stale"
            ? 1800
            : 20,
      thresholdSeconds: 300,
      thresholdSource: "Default",
      ...(status === "Missing" ? { reason: "NeverReceived" } : {}),
    },
    alarm: { status: status === "NotMonitored" ? "Unknown" : "Normal" },
    gateway: { id: "GW-SOS-001", connected: true },
    monitoring: { monitored: status !== "NotMonitored", ...extra },
    deviceDtId: "dev-ahu-1",
    deviceName: "AHU-1",
    spaceDtId: "space-office",
    spaceName: "執務室",
    floorDtId: "floor-3",
    floorName: "3F",
    buildingDtId: "bldg-sos",
    buildingName: "SOS ビル",
    tags: [],
  });
  const items = [
    item("SOS-PT-002", "還気温度", "Stale"),
    item("SOS-PT-007", "外気温度", "Missing"),
    item("SOS-PT-004-REL", "設定温度 解除", "NotMonitored", {
      reason: "ReleasePoint",
    }),
    item("SOS-PT-009", "運転指令（書込専用）", "NotMonitored", {
      reason: "DeclaredNoTelemetry",
    }),
  ];
  await page.route("**/buildings*", (route) =>
    fulfillJson(route, [
      { dtId: "bldg-sos", id: "bldg-sos", name: "SOS ビル" },
    ]),
  );
  await page.route("**/api/v1/telemetry/health/summary*", (route) =>
    fulfillJson(route, {
      totalPoints: 8,
      fresh: 6,
      stale: 1,
      missing: 1,
      unknown: 0,
      alarmWarn: 0,
      alarmCritical: 0,
      dataComplete: true,
      indexState: "Ready",
      notMonitored: 2,
    }),
  );
  await page.route(/\/api\/v1\/telemetry\/health(\?.*)?$/, (route) =>
    fulfillJson(route, {
      items,
      total: items.length,
      limit: 50,
      offset: 0,
      dataComplete: true,
      indexState: "Ready",
    }),
  );
  await page.route("**/api/v1/telemetry/config*", (route) =>
    fulfillJson(route, {
      staleThresholdSeconds: 300,
      staleIntervalMultiplier: 3,
    }),
  );
  await page.goto("/health?includeUnmonitored=true");
  await page.getByText("設定温度 解除").first().waitFor();
  await hideDevOverlay(page);
  await page.waitForTimeout(400);
  await page.screenshot({
    path: `${SHOTS}/health-not-monitored.png`,
    fullPage: true,
  });
});
