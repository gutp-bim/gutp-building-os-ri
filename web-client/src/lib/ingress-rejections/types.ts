/**
 * Shape of `GET /api/system/ingress-rejections`
 * (BuildingOS.Shared.Infrastructure.Monitoring.IngressRejectionStats). Serialized camelCase by
 * ASP.NET Core. `rejections` is empty and `metricsAvailable` is false when the metrics backend
 * (Prometheus) is unavailable — the panel degrades gracefully rather than failing (#292).
 */
export interface IngressRejectionCount {
  reason: string;
  count: number;
}

export interface IngressRejectionStats {
  rejections: IngressRejectionCount[];
  metricsAvailable: boolean;
}
