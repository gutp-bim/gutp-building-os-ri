import {
  formatKpi,
  serviceDotClass,
  serviceLabel,
} from "@/lib/system-status/format";
import type { SystemStatus } from "@/lib/system-status/types";
import { HelpButton } from "@/components/help/help-button";

/**
 * Pure, at-a-glance presentation of the platform status (#146). Takes the resolved data as props (no
 * fetching) so the display logic is unit-testable. Built to be useful without Grafana — KPI cards
 * degrade to "—" when the metrics backend is unavailable, and a Grafana deep link is shown only when
 * a URL is configured.
 */
export function SystemStatusView({
  status,
  grafanaUrl,
  updatedAt,
}: {
  status: SystemStatus;
  grafanaUrl?: string | null;
  updatedAt?: Date | null;
}) {
  return (
    <div className="container mx-auto px-4 py-8" data-testid="system-status">
      <div className="mb-4 flex items-center justify-between">
        <div className="flex items-center gap-2">
          <h1 className="text-2xl font-bold">システム稼働状態</h1>
          <HelpButton helpKey="platform.status" />
        </div>
        {updatedAt ? (
          <span className="text-sm text-gray-600" data-testid="updated-at">
            最終更新: {updatedAt.toLocaleTimeString()}
          </span>
        ) : null}
      </div>

      {!status.metricsAvailable ? (
        <p
          className="mb-4 rounded border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800"
          data-testid="metrics-unavailable"
        >
          メトリクスバックエンド（Prometheus）が未接続のため、KPI は表示できません。サービスの
          稼働状態は引き続き取得しています。
        </p>
      ) : null}

      <section className="mb-8">
        <h2 className="mb-3 text-lg font-semibold">サービス</h2>
        <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
          {(status.services ?? []).map((s) => (
            <li
              key={s.name}
              className="flex items-center gap-3 rounded border border-gray-200 px-3 py-2"
              data-testid={`service-${s.name}`}
            >
              <span
                className={`inline-block h-3 w-3 rounded-full ${serviceDotClass(s.status)}`}
                aria-hidden="true"
              />
              <span className="flex-1 font-medium">{s.name}</span>
              <span className="text-sm text-gray-600">{serviceLabel(s.status)}</span>
            </li>
          ))}
        </ul>
      </section>

      <section className="mb-8">
        <h2 className="mb-3 text-lg font-semibold">KPI（直近）</h2>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <KpiCard
            testId="kpi-msg-rate"
            label="メッセージ流量 (1m)"
            value={formatKpi(status.kpis?.msgRate1m, { suffix: " msg/s" })}
          />
          <KpiCard
            testId="kpi-control-req"
            label="制御リクエスト (5m)"
            value={formatKpi(status.kpis?.controlReq5m, { suffix: " 件" })}
          />
        </div>
      </section>

      {grafanaUrl ? (
        <a
          href={grafanaUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="text-sm text-blue-600 underline"
          data-testid="grafana-link"
        >
          Grafana で詳細を見る →
        </a>
      ) : null}
    </div>
  );
}

function KpiCard({
  label,
  value,
  testId,
}: {
  label: string;
  value: string;
  testId: string;
}) {
  return (
    <div className="rounded border border-gray-200 px-4 py-3" data-testid={testId}>
      <div className="text-sm text-gray-600">{label}</div>
      <div className="text-2xl font-bold">{value}</div>
    </div>
  );
}
