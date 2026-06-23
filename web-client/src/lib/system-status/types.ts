/**
 * Shape of `GET /api/system/status` (BuildingOS.Shared.Infrastructure.Monitoring.SystemStatus).
 * Serialized camelCase by ASP.NET Core. KPI values are null when the metrics backend (Prometheus)
 * is unavailable — the dashboard degrades gracefully rather than failing (#144 / #146).
 */
export type ServiceState = "up" | "down" | "unknown";

export interface ServiceStatus {
  name: string;
  /** Raw status string from the API ("up" / "down" / …); normalise via `toServiceState`. */
  status: string;
}

export interface SystemKpis {
  msgRate1m: number | null;
  controlReq5m: number | null;
}

export interface SystemStatus {
  services: ServiceStatus[];
  kpis: SystemKpis;
  metricsAvailable: boolean;
}
