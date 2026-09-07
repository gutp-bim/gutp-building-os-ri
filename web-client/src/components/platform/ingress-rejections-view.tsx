import type { IngressRejectionStats } from "@/lib/ingress-rejections/types";
import { HelpButton } from "@/components/help/help-button";

/**
 * Pure, at-a-glance presentation of gRPC gateway-ingress rejection counts by reason (#292: the
 * accept/reject hierarchy policy is otherwise only visible in Prometheus/logs). Takes the resolved
 * data as props (no fetching) so the display logic is unit-testable. Degrades gracefully when the
 * metrics backend (Prometheus) is unavailable — same convention as {@link SystemStatusView}.
 */
export function IngressRejectionsView({
  stats,
  updatedAt,
}: {
  stats: IngressRejectionStats;
  updatedAt?: Date | null;
}) {
  return (
    <div className="container mx-auto px-4 py-8" data-testid="ingress-rejections">
      <div className="mb-4 flex items-center justify-between">
        <div className="flex items-center gap-2">
          <h1 className="text-2xl font-bold">取込拒否件数</h1>
          <HelpButton helpKey="platform.ingressRejections" />
        </div>
        {updatedAt ? (
          <span className="text-sm text-gray-600" data-testid="updated-at">
            最終更新: {updatedAt.toLocaleTimeString()}
          </span>
        ) : null}
      </div>

      {!stats.metricsAvailable ? (
        <p
          className="mb-4 rounded border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800"
          data-testid="metrics-unavailable"
        >
          メトリクスバックエンド（Prometheus）が未接続のため、拒否件数は表示できません。
          ゲートウェイからのテレメトリー受理/拒否そのものは引き続き動作しています。
        </p>
      ) : stats.rejections.length === 0 ? (
        <p className="text-gray-600" data-testid="no-rejections">
          直近の拒否はありません。
        </p>
      ) : (
        <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
          {stats.rejections.map((r) => (
            <li
              key={r.reason}
              className="flex items-center justify-between rounded border border-gray-200 px-3 py-2"
              data-testid={`rejection-${r.reason}`}
            >
              <span className="font-mono text-sm">{r.reason}</span>
              <span className="text-lg font-bold">
                {r.count.toLocaleString("ja-JP")}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
