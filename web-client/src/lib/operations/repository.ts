/**
 * `GET /api/operations/summary`（#451 Phase 1）のアクセス façade。
 *
 * Platform 由来（Prometheus 集計）のデータ流量 KPI だけを持つ — Point 母数 / Fresh 率は
 * `@/lib/health/repository`（`telemetry/health/summary`, #452）の責務で、ここには含めない。
 */

import { apiClient } from "@/lib/infra/aspida-client";

/** データ流量 KPI。Prometheus 未配線時はどちらも null（`metricsAvailable` が false）。 */
export type OperationsSummary = {
  msgRate1m: number | null;
  msgRate1hAvg: number | null;
  metricsAvailable: boolean;
};

export async function fetchOperationsSummary(
  token?: string,
): Promise<OperationsSummary> {
  const wire = await apiClient(token).api.operations.summary.$get();
  return {
    msgRate1m: wire.msgRate1m ?? null,
    msgRate1hAvg: wire.msgRate1hAvg ?? null,
    metricsAvailable: wire.metricsAvailable ?? false,
  };
}
