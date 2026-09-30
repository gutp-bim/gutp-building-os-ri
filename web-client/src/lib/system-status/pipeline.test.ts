import { describe, expect, it } from "vitest";
import {
  DEFAULT_PIPELINE_THRESHOLDS,
  buildPipelineKpis,
  formatBreakdown,
  formatSeconds,
  grafanaKpiLink,
  kpiLevel,
  kpiLevelClass,
} from "./pipeline";
import type { SystemStatus } from "./types";

const full: SystemStatus = {
  services: [],
  metricsAvailable: true,
  kpis: {
    msgRate1m: 1831,
    controlReq5m: 37,
    ingressRate1m: 1842,
    ingressBySource: [
      { label: "gateway-grpc", value: 1800 },
      { label: "mqtt", value: 42 },
    ],
    validatedRate1m: 1831,
    rejectedRate1m: 11,
    rejectedPercent: 0.6,
    rejectedByResult: [
      { label: "unknown_point", value: 8 },
      { label: "bad_payload", value: 3 },
    ],
    eventLagP95Seconds: 1.2,
    consumerLagP95Seconds: 0.082,
    parquetFreshnessP95Seconds: 28,
    parquetDropped15m: 0,
    natsPending: 124,
  },
  thresholds: DEFAULT_PIPELINE_THRESHOLDS,
};

const byKey = (status: SystemStatus, grafanaUrl?: string | null) =>
  Object.fromEntries(
    buildPipelineKpis(status, grafanaUrl).map((k) => [k.key, k]),
  );

describe("kpiLevel", () => {
  it("is nodata for null/undefined/NaN", () => {
    expect(kpiLevel(null, 1)).toBe("nodata");
    expect(kpiLevel(undefined, 1)).toBe("nodata");
    expect(kpiLevel(Number.NaN, 1)).toBe("nodata");
  });
  it("is warn strictly above the threshold, ok at or below it", () => {
    expect(kpiLevel(31, 30)).toBe("warn");
    expect(kpiLevel(30, 30)).toBe("ok");
    expect(kpiLevel(0, 30)).toBe("ok");
  });
  it("is neutral when the KPI has no threshold", () => {
    expect(kpiLevel(5, undefined)).toBe("neutral");
  });
});

describe("kpiLevelClass", () => {
  it("maps warn → amber, ok → green, nodata → gray", () => {
    expect(kpiLevelClass("warn")).toContain("amber");
    expect(kpiLevelClass("ok")).toContain("green");
    expect(kpiLevelClass("nodata")).toContain("gray");
    expect(kpiLevelClass("neutral")).not.toContain("amber");
  });
});

describe("formatSeconds", () => {
  it("renders sub-second values in ms, seconds with one decimal, minutes beyond 60s", () => {
    expect(formatSeconds(0.082)).toBe("82 ms");
    expect(formatSeconds(1.2)).toBe("1.2 s");
    expect(formatSeconds(28)).toBe("28 s");
    expect(formatSeconds(125)).toBe("2.1 分");
  });
  it("renders an em dash without data", () => {
    expect(formatSeconds(null)).toBe("—");
    expect(formatSeconds(undefined)).toBe("—");
  });
});

describe("formatBreakdown", () => {
  it("lists one label per line", () => {
    expect(
      formatBreakdown(
        [
          { label: "mqtt", value: 42 },
          { label: "amqp", value: 1.5 },
        ],
        " msg/s",
      ),
    ).toBe("mqtt: 42 msg/s\namqp: 1.5 msg/s");
  });
  it("is null for an empty or missing breakdown", () => {
    expect(formatBreakdown([], "")).toBeNull();
    expect(formatBreakdown(null, "")).toBeNull();
  });
});

describe("grafanaKpiLink", () => {
  it("is null when Grafana is not configured", () => {
    expect(grafanaKpiLink(null, "validated")).toBeNull();
    expect(grafanaKpiLink("", "eventLag")).toBeNull();
  });
  it("deep-links the connector panel for validated and the overview for the rest", () => {
    expect(grafanaKpiLink("https://grafana.example/", "validated")).toBe(
      "https://grafana.example/d/building-os-overview/building-os-overview?viewPanel=5",
    );
    expect(grafanaKpiLink("https://grafana.example", "eventLag")).toBe(
      "https://grafana.example/d/building-os-overview/building-os-overview",
    );
  });
  it("derives the Grafana base from a dashboard URL", () => {
    expect(
      grafanaKpiLink(
        "https://grafana.example/sub/d/abc/x?orgId=1",
        "natsPending",
      ),
    ).toBe(
      "https://grafana.example/sub/d/building-os-overview/building-os-overview",
    );
  });
});

describe("buildPipelineKpis", () => {
  it("lists the eight pipeline KPIs in display order", () => {
    expect(buildPipelineKpis(full).map((k) => k.key)).toEqual([
      "ingress",
      "validated",
      "rejected",
      "eventLag",
      "consumerLag",
      "parquetFreshness",
      "natsPending",
      "controlReq",
    ]);
  });

  it("formats values and keeps every KPI ok under the default thresholds", () => {
    const k = byKey(full);
    expect(k.ingress.value).toBe("1,842 msg/s");
    expect(k.validated.value).toBe("1,831 msg/s");
    expect(k.rejected.value).toBe("11 msg/s (0.6 %)");
    expect(k.eventLag.value).toBe("1.2 s");
    expect(k.consumerLag.value).toBe("82 ms");
    expect(k.parquetFreshness.value).toBe("28 s");
    expect(k.natsPending.value).toBe("124");
    expect(k.controlReq.value).toBe("37 件");
    for (const key of [
      "rejected",
      "eventLag",
      "consumerLag",
      "parquetFreshness",
      "natsPending",
    ]) {
      expect(k[key].level).toBe("ok");
    }
    expect(k.ingress.level).toBe("neutral");
  });

  it("puts the breakdowns in the tooltip", () => {
    const k = byKey(full);
    expect(k.ingress.tooltip).toContain("gateway-grpc: 1,800 msg/s");
    expect(k.rejected.tooltip).toContain("unknown_point: 8 msg/s");
    expect(k.rejected.tooltip).toContain("bad_payload: 3 msg/s");
  });

  it("warns against the thresholds carried in the response", () => {
    const k = byKey({
      ...full,
      kpis: {
        ...full.kpis,
        rejectedPercent: 2.5,
        eventLagP95Seconds: 45,
        consumerLagP95Seconds: 6,
        natsPending: 20000,
      },
      thresholds: {
        ...DEFAULT_PIPELINE_THRESHOLDS,
        parquetFreshnessWarnSeconds: 20,
      },
    });
    expect(k.rejected.level).toBe("warn");
    expect(k.eventLag.level).toBe("warn");
    expect(k.consumerLag.level).toBe("warn");
    expect(k.natsPending.level).toBe("warn");
    expect(k.parquetFreshness.level).toBe("warn");
  });

  it("warns on Parquet freshness when rows were dropped even if freshness itself is fine", () => {
    const k = byKey({ ...full, kpis: { ...full.kpis, parquetDropped15m: 3 } });
    expect(k.parquetFreshness.level).toBe("warn");
    expect(k.parquetFreshness.tooltip).toContain("3");
  });

  describe("Parquet flush stall (#456)", () => {
    const withFreshness = (
      parquetFreshnessP95Seconds: number | null,
      parquetFlushStalled: boolean | null | undefined,
      metricsAvailable = true,
    ) =>
      byKey({
        ...full,
        metricsAvailable,
        kpis: { ...full.kpis, parquetFreshnessP95Seconds, parquetFlushStalled },
      });

    it("is ok when flushes are happening", () => {
      const k = withFreshness(28, false);
      expect(k.parquetFreshness.level).toBe("ok");
      expect(k.parquetFreshness.value).toBe("28 s");
    });

    it("warns with distinct text when the writer stopped flushing (p95 has no samples)", () => {
      const k = withFreshness(null, true);
      expect(k.parquetFreshness.level).toBe("warn");
      expect(k.parquetFreshness.value).toBe("flush 停止の可能性");
      expect(k.parquetFreshness.tooltip).toContain("flush");
    });

    it("warns on a stall even while an older p95 is still in the window", () => {
      const k = withFreshness(28, true);
      expect(k.parquetFreshness.level).toBe("warn");
      expect(k.parquetFreshness.value).toContain("flush 停止の可能性");
    });

    it("stays nodata without Prometheus", () => {
      const k = withFreshness(null, null, false);
      expect(k.parquetFreshness.level).toBe("nodata");
      expect(k.parquetFreshness.value).toBe("—");
    });

    it("stays nodata without traffic (not stalled)", () => {
      const k = withFreshness(null, false);
      expect(k.parquetFreshness.level).toBe("nodata");
    });
  });

  it("falls back to the default thresholds for a response without them", () => {
    const k = byKey({
      ...full,
      thresholds: undefined,
      kpis: { ...full.kpis, eventLagP95Seconds: 31 },
    });
    expect(k.eventLag.level).toBe("warn");
  });

  it("degrades every KPI to an em dash / nodata without metrics (older API shape too)", () => {
    const k = byKey({
      services: [],
      metricsAvailable: false,
      kpis: { msgRate1m: null, controlReq5m: null },
    });
    for (const kpi of Object.values(k)) {
      expect(kpi.value).toBe("—");
      expect(["nodata", "neutral"]).toContain(kpi.level);
      expect(kpi.level).not.toBe("warn");
    }
    expect(k.rejected.level).toBe("nodata");
  });

  it("uses msgRate1m for validated when an older API omits validatedRate1m", () => {
    const k = byKey({
      services: [],
      metricsAvailable: true,
      kpis: { msgRate1m: 1240, controlReq5m: 3 },
    });
    expect(k.validated.value).toBe("1,240 msg/s");
  });

  it("attaches Grafana links only when configured", () => {
    expect(byKey(full).validated.grafanaHref).toBeNull();
    expect(byKey(full, "https://g.example").validated.grafanaHref).toContain(
      "viewPanel=5",
    );
  });
});
