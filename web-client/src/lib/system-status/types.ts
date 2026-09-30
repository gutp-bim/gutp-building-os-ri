/**
 * Shape of `GET /api/v1/system/status` (BuildingOS.Shared.Infrastructure.Monitoring.SystemStatus).
 * Serialized camelCase by ASP.NET Core. KPI values are null when the metrics backend (Prometheus)
 * is unavailable — the dashboard degrades gracefully rather than failing (#144 / #146).
 *
 * The pipeline KPIs (#456) are additive on the wire; they are optional here so a response from an
 * older API server (only `msgRate1m` / `controlReq5m`) still type-checks and renders.
 */
export type ServiceState = "up" | "down" | "unknown";

export interface ServiceStatus {
  name: string;
  /** Raw status string from the API ("up" / "down" / …); normalise via `toServiceState`. */
  status: string;
}

/** One bucket of a KPI breakdown (e.g. ingress msg/s for `source=mqtt`). */
export interface KpiBreakdownItem {
  label: string;
  value: number;
}

export interface SystemKpis {
  /** Validated msg/s (kept for compatibility; same value as `validatedRate1m`). */
  msgRate1m: number | null;
  controlReq5m: number | null;
  ingressRate1m?: number | null;
  ingressBySource?: KpiBreakdownItem[] | null;
  validatedRate1m?: number | null;
  /** Measured directly on `ingress.messages{result!="published"}` — never ingress − validated. */
  rejectedRate1m?: number | null;
  /** rejected / ingress × 100; null when there is no ingress traffic. */
  rejectedPercent?: number | null;
  rejectedByResult?: KpiBreakdownItem[] | null;
  eventLagP95Seconds?: number | null;
  consumerLagP95Seconds?: number | null;
  parquetFreshnessP95Seconds?: number | null;
  parquetDropped15m?: number | null;
  natsPending?: number | null;
}

/** Effective warn thresholds (SettingsRegistry `platform.kpi.*`, editable in /platform/settings). */
export interface PipelineKpiThresholds {
  rejectedPercentWarn: number;
  eventLagP95WarnSeconds: number;
  consumerLagP95WarnSeconds: number;
  parquetFreshnessWarnSeconds: number;
  natsPendingWarn: number;
}

export interface SystemStatus {
  services: ServiceStatus[];
  kpis: SystemKpis;
  metricsAvailable: boolean;
  thresholds?: PipelineKpiThresholds | null;
}
