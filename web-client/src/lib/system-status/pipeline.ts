import { formatKpi } from "./format";
import type {
  KpiBreakdownItem,
  PipelineKpiThresholds,
  SystemStatus,
} from "./types";

/**
 * Pure view-model logic for the /platform/status "Pipeline Health" KPIs (#456): value formatting,
 * threshold → level → colour, tooltip breakdowns and per-KPI Grafana deep links. The component only
 * renders what {@link buildPipelineKpis} returns.
 */

/**
 * `ok` / `warn` = compared against a threshold; `neutral` = a volume KPI with no threshold (ingress,
 * validated, control); `nodata` = no value (Prometheus absent, or no samples in the window).
 */
export type KpiLevel = "ok" | "warn" | "neutral" | "nodata";

export type PipelineKpiKey =
  | "ingress"
  | "validated"
  | "rejected"
  | "eventLag"
  | "consumerLag"
  | "parquetFreshness"
  | "natsPending"
  | "controlReq";

export interface PipelineKpiCard {
  key: PipelineKpiKey;
  /** Stable test id (kept as `kpi-msg-rate` / `kpi-control-req` for the two pre-#456 cards). */
  testId: string;
  label: string;
  /** Glossary term shown as the label's tooltip (help content, #149). */
  glossaryTerm: string;
  value: string;
  level: KpiLevel;
  /** Native-tooltip text: breakdown and/or the threshold in effect. */
  tooltip: string | null;
  grafanaHref: string | null;
}

/**
 * Mirror of the SettingsRegistry `platform.kpi.*` defaults — used only when an older API server does
 * not send `thresholds`. The server's effective values (admin overrides included) always win.
 */
export const DEFAULT_PIPELINE_THRESHOLDS: PipelineKpiThresholds = {
  rejectedPercentWarn: 1,
  eventLagP95WarnSeconds: 30,
  consumerLagP95WarnSeconds: 5,
  parquetFreshnessWarnSeconds: 600,
  natsPendingWarn: 1000,
};

function hasValue(value: number | null | undefined): value is number {
  return value !== null && value !== undefined && !Number.isNaN(value);
}

/** Level of a KPI value against its warn threshold (strictly above = warn). */
export function kpiLevel(
  value: number | null | undefined,
  warnAbove: number | undefined,
): KpiLevel {
  if (!hasValue(value)) return "nodata";
  if (warnAbove === undefined) return "neutral";
  return value > warnAbove ? "warn" : "ok";
}

/** Tailwind classes for a KPI card by level (border + value colour). */
export function kpiLevelClass(level: KpiLevel): string {
  switch (level) {
    case "warn":
      return "border-amber-400 bg-amber-50 text-amber-800";
    case "ok":
      return "border-green-300 text-green-700";
    case "nodata":
      return "border-gray-200 text-gray-400";
    default:
      return "border-gray-200 text-gray-900";
  }
}

/** Seconds → "82 ms" / "1.2 s" / "2.1 分"; em dash without data. */
export function formatSeconds(value: number | null | undefined): string {
  if (!hasValue(value)) return "—";
  if (value < 1) return `${Math.round(value * 1000)} ms`;
  if (value < 60) return `${formatKpi(value, { maximumFractionDigits: 1 })} s`;
  return `${formatKpi(value / 60, { maximumFractionDigits: 1 })} 分`;
}

/** Breakdown → one "label: value" line each (for a native tooltip); null when empty. */
export function formatBreakdown(
  items: KpiBreakdownItem[] | null | undefined,
  suffix: string,
): string | null {
  if (!items || items.length === 0) return null;
  return items
    .map((i) => `${i.label}: ${formatKpi(i.value, { suffix })}`)
    .join("\n");
}

const OVERVIEW_DASHBOARD = "d/building-os-overview/building-os-overview";

/**
 * Only "Connector Processing Rate" (panel 5) of the provisioned Building OS Overview dashboard
 * matches a KPI one-to-one. The other KPIs have no dedicated panel yet, so they link the overview
 * dashboard itself (observability-baseline.md §"KPIs and PromQL used by the Platform UI").
 */
const GRAFANA_PANEL: Partial<Record<PipelineKpiKey, number>> = { validated: 5 };

/**
 * Per-KPI Grafana deep link, or null when `NEXT_PUBLIC_GRAFANA_URL` is unset. Accepts either the
 * Grafana base URL or any dashboard URL under it (the part before `/d/` is taken as the base).
 */
export function grafanaKpiLink(
  grafanaUrl: string | null | undefined,
  key: PipelineKpiKey,
): string | null {
  if (!grafanaUrl) return null;
  const withoutQuery = grafanaUrl.split(/[?#]/)[0];
  const dashIdx = withoutQuery.indexOf("/d/");
  const base = (
    dashIdx >= 0 ? withoutQuery.slice(0, dashIdx) : withoutQuery
  ).replace(/\/+$/, "");
  const panel = GRAFANA_PANEL[key];
  return `${base}/${OVERVIEW_DASHBOARD}${panel !== undefined ? `?viewPanel=${panel}` : ""}`;
}

const FLUSH_STALLED_TEXT = "flush 停止の可能性";

function joinTooltip(...parts: (string | null | undefined)[]): string | null {
  const present = parts.filter((p): p is string => !!p);
  return present.length > 0 ? present.join("\n") : null;
}

function worst(a: KpiLevel, b: KpiLevel): KpiLevel {
  if (a === "warn" || b === "warn") return "warn";
  return a;
}

/** Builds the eight Pipeline Health cards, in display order, from a status response. */
export function buildPipelineKpis(
  status: SystemStatus,
  grafanaUrl?: string | null,
): PipelineKpiCard[] {
  const k = status.kpis ?? { msgRate1m: null, controlReq5m: null };
  const t = status.thresholds ?? DEFAULT_PIPELINE_THRESHOLDS;
  const link = (key: PipelineKpiKey) => grafanaKpiLink(grafanaUrl, key);
  const validated = hasValue(k.validatedRate1m)
    ? k.validatedRate1m
    : k.msgRate1m;

  const rejectedValue = hasValue(k.rejectedRate1m)
    ? `${formatKpi(k.rejectedRate1m, { suffix: " msg/s" })}${
        hasValue(k.rejectedPercent)
          ? ` (${formatKpi(k.rejectedPercent, { suffix: " %", maximumFractionDigits: 1 })})`
          : ""
      }`
    : "—";
  // With traffic, colour by the rejection *rate*; without traffic there is no rate to judge.
  const rejectedLevel = hasValue(k.rejectedRate1m)
    ? hasValue(k.rejectedPercent)
      ? kpiLevel(k.rejectedPercent, t.rejectedPercentWarn)
      : "neutral"
    : "nodata";

  const flushStalled = k.parquetFlushStalled === true;
  // A stalled writer records no freshness samples, so its p95 goes null — without the stall flag
  // that would read as grey "no data", indistinguishable from Prometheus being absent.
  const droppedLevel: KpiLevel =
    flushStalled || (hasValue(k.parquetDropped15m) && k.parquetDropped15m > 0)
      ? "warn"
      : "ok";
  const freshnessLevel = hasValue(k.parquetFreshnessP95Seconds)
    ? worst(
        kpiLevel(k.parquetFreshnessP95Seconds, t.parquetFreshnessWarnSeconds),
        droppedLevel,
      )
    : droppedLevel === "warn"
      ? "warn"
      : "nodata";

  return [
    {
      key: "ingress",
      testId: "kpi-ingress",
      label: "Ingress (1m)",
      glossaryTerm: "Ingress レート",
      value: formatKpi(k.ingressRate1m, { suffix: " msg/s" }),
      level: kpiLevel(k.ingressRate1m, undefined),
      tooltip: formatBreakdown(k.ingressBySource, " msg/s"),
      grafanaHref: link("ingress"),
    },
    {
      key: "validated",
      testId: "kpi-msg-rate",
      label: "Validated (1m)",
      glossaryTerm: "Validated レート",
      value: formatKpi(validated, { suffix: " msg/s" }),
      level: kpiLevel(validated, undefined),
      tooltip: null,
      grafanaHref: link("validated"),
    },
    {
      key: "rejected",
      testId: "kpi-rejected",
      label: "Rejected (1m)",
      glossaryTerm: "Rejected レート",
      value: rejectedValue,
      level: rejectedLevel,
      tooltip: joinTooltip(
        formatBreakdown(k.rejectedByResult, " msg/s"),
        `警告: 拒否率 > ${formatKpi(t.rejectedPercentWarn)} %（累計件数は /platform/ingress-rejections）`,
      ),
      grafanaHref: link("rejected"),
    },
    {
      key: "eventLag",
      testId: "kpi-event-lag",
      label: "Event lag p95",
      glossaryTerm: "Event lag",
      value: formatSeconds(k.eventLagP95Seconds),
      level: kpiLevel(k.eventLagP95Seconds, t.eventLagP95WarnSeconds),
      tooltip: `警告: > ${formatSeconds(t.eventLagP95WarnSeconds)}`,
      grafanaHref: link("eventLag"),
    },
    {
      key: "consumerLag",
      testId: "kpi-consumer-lag",
      label: "Consumer lag p95",
      glossaryTerm: "Consumer lag",
      value: formatSeconds(k.consumerLagP95Seconds),
      level: kpiLevel(k.consumerLagP95Seconds, t.consumerLagP95WarnSeconds),
      tooltip: `警告: > ${formatSeconds(t.consumerLagP95WarnSeconds)}`,
      grafanaHref: link("consumerLag"),
    },
    {
      key: "parquetFreshness",
      testId: "kpi-parquet-freshness",
      label: "Parquet freshness p95",
      glossaryTerm: "Parquet 鮮度",
      value: flushStalled
        ? hasValue(k.parquetFreshnessP95Seconds)
          ? `${formatSeconds(k.parquetFreshnessP95Seconds)}（${FLUSH_STALLED_TEXT}）`
          : FLUSH_STALLED_TEXT
        : formatSeconds(k.parquetFreshnessP95Seconds),
      level: freshnessLevel,
      tooltip: joinTooltip(
        flushStalled
          ? "validated テレメトリは流れているのに、検知窓（max(警告閾値, 15 分)）の間 Parquet の flush が 1 回もありません。lake writer と MinIO を確認してください。"
          : null,
        hasValue(k.parquetDropped15m)
          ? `破棄行 (15m): ${formatKpi(k.parquetDropped15m)}（> 0 で警告）`
          : null,
        `警告: > ${formatSeconds(t.parquetFreshnessWarnSeconds)}`,
      ),
      grafanaHref: link("parquetFreshness"),
    },
    {
      key: "natsPending",
      testId: "kpi-nats-pending",
      label: "NATS pending",
      glossaryTerm: "NATS pending",
      value: formatKpi(k.natsPending),
      level: kpiLevel(k.natsPending, t.natsPendingWarn),
      tooltip: `警告: > ${formatKpi(t.natsPendingWarn)}（NATS exporter 未配線なら —）`,
      grafanaHref: link("natsPending"),
    },
    {
      key: "controlReq",
      testId: "kpi-control-req",
      label: "Control req (5m)",
      glossaryTerm: "制御リクエスト数",
      value: formatKpi(k.controlReq5m, { suffix: " 件" }),
      level: kpiLevel(k.controlReq5m, undefined),
      tooltip: null,
      grafanaHref: link("controlReq"),
    },
  ];
}
