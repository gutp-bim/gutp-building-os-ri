import type { TelemetryStatePoint } from "@/lib/telemetry/types";

/**
 * Non-numeric state/text history (#152 Phase B). Charts stay numeric-only; a string/boolean point's
 * history is shown here as a timeline of readings (newest first) with the state value as a chip. Fed
 * the pre-mapped {@link TelemetryStatePoint}s from the page (via `queryStateSeries`), so this stays a
 * pure presentational component. Rendered only when there are non-numeric readings.
 */
export function TelemetryStateTimeline({
  points,
  loading,
}: {
  points: TelemetryStatePoint[];
  loading: boolean;
}) {
  // Newest first, without mutating the caller's ascending array.
  const rows = [...points].reverse();

  return (
    <section
      data-testid="state-timeline"
      className="mt-8 rounded-lg border border-gray-200 p-4"
    >
      <h2 className="mb-3 text-lg font-semibold text-gray-800">状態履歴</h2>
      {loading ? (
        <p className="text-sm text-gray-600">読み込み中…</p>
      ) : rows.length === 0 ? (
        <p data-testid="state-timeline-empty" className="text-sm text-gray-600">
          状態の履歴はありません。
        </p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-200 text-left text-gray-700">
                <th className="py-2 pr-4 font-medium">日時</th>
                <th className="py-2 font-medium">状態</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((p, i) => (
                <tr
                  key={`${p.t}-${i}`}
                  data-testid="state-timeline-row"
                  className="border-b border-gray-100"
                >
                  <td className="py-2 pr-4 text-gray-800">
                    {new Date(p.t).toLocaleString("ja-JP")}
                  </td>
                  <td className="py-2">
                    <span className="inline-flex rounded-full bg-blue-100 px-2 py-0.5 text-xs font-medium text-blue-800">
                      {p.state}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
