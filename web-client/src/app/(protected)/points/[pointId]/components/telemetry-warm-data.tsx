import { InlineBanner } from "@/components/ui/inline-banner";
import { ValidTelemetryData } from "@/lib/infra/aspida-client/generated/@types";
import {
  GRANULARITY_OPTIONS,
  PERIOD_OPTIONS,
  spansMultipleDays,
  type GranularityOption,
  type PeriodValue,
} from "@/lib/telemetry/range";
import { ArrowPathIcon } from "@heroicons/react/24/outline";
import {
  CartesianGrid,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  TooltipProps,
  XAxis,
  YAxis,
} from "recharts";

// Brand series colour (Tailwind blue-500), replacing Recharts' default purple (#8884d8).
const SERIES_COLOR = "#3b82f6";

const formatAxis = (datetime: string, multiDay: boolean) => {
  const d = new Date(datetime);
  const pad = (n: number) => n.toString().padStart(2, "0");
  return multiDay
    ? `${pad(d.getMonth() + 1)}/${pad(d.getDate())}`
    : `${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

function makeTooltip(unit?: string) {
  return function CustomTooltip({
    active,
    payload,
  }: TooltipProps<number, string>) {
    if (active && payload && payload.length) {
      return (
        <div className="bg-white p-2 border border-gray-200 shadow-sm">
          <p className="text-sm text-gray-600">
            {payload[0].payload.fullDatetime}
          </p>
          <p className="text-sm font-semibold">
            {`値: ${payload[0].value?.toFixed(1)}${unit ? ` ${unit}` : ""}`}
          </p>
        </div>
      );
    }
    return null;
  };
}

export function TelemetryWarmData({
  warmData,
  warmLoading,
  onRefresh,
  period,
  granularity,
  onPeriodChange,
  onGranularityChange,
  unit,
  customStart = "",
  customEnd = "",
  onCustomStartChange,
  onCustomEndChange,
  rangeError,
  multiDay: multiDayProp,
}: {
  warmData: ValidTelemetryData[];
  warmLoading: boolean;
  onRefresh: () => void;
  period: PeriodValue;
  granularity: GranularityOption;
  onPeriodChange: (period: PeriodValue) => void;
  onGranularityChange: (granularity: GranularityOption) => void;
  unit?: string;
  /** Custom-range inputs — only rendered/used when `period === "custom"`. */
  customStart?: string;
  customEnd?: string;
  onCustomStartChange?: (value: string) => void;
  onCustomEndChange?: (value: string) => void;
  /** Guard message for the custom range (start ≥ end / future); shown inline, blocks the query. */
  rangeError?: string | null;
  /** Axis date-vs-time hint; the parent supplies it for a custom range, presets derive it here. */
  multiDay?: boolean;
}) {
  const nowMax = new Date().toISOString().slice(0, 16);
  const multiDay =
    multiDayProp ?? (period !== "custom" ? spansMultipleDays(period) : false);
  const chartData = [...warmData]
    .sort(
      (a, b) =>
        new Date(a.datetime || "").getTime() -
        new Date(b.datetime || "").getTime(),
    )
    .map((data) => ({
      time: data.datetime ? formatAxis(data.datetime, multiDay) : "",
      value: data.value,
      fullDatetime: data.datetime
        ? new Date(data.datetime).toLocaleString("ja-JP")
        : "",
    }));

  return (
    <div className="bg-white p-4 rounded-lg shadow">
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-sm font-medium text-gray-700">
          テレメトリ履歴{unit ? `（単位: ${unit}）` : ""}
        </h3>
        <div className="flex items-center gap-2">
          <label className="flex items-center gap-1 text-xs text-gray-600">
            期間
            <select
              data-testid="warm-period-select"
              aria-label="期間"
              value={period}
              onChange={(e) => onPeriodChange(e.target.value as PeriodValue)}
              className="rounded border border-gray-300 px-2 py-1 text-sm"
            >
              {PERIOD_OPTIONS.map((p) => (
                <option key={p.value} value={p.value}>
                  {p.label}
                </option>
              ))}
            </select>
          </label>
          <label className="flex items-center gap-1 text-xs text-gray-600">
            粒度
            <select
              data-testid="warm-granularity-select"
              aria-label="粒度"
              value={granularity}
              onChange={(e) =>
                onGranularityChange(e.target.value as GranularityOption)
              }
              className="rounded border border-gray-300 px-2 py-1 text-sm"
            >
              {GRANULARITY_OPTIONS.map((g) => (
                <option key={g.value} value={g.value}>
                  {g.label}
                </option>
              ))}
            </select>
          </label>
          <button
            onClick={onRefresh}
            disabled={warmLoading}
            aria-label="再読み込み"
            className="text-gray-600 hover:text-gray-900 disabled:opacity-50"
          >
            <ArrowPathIcon
              className={`h-5 w-5 ${warmLoading ? "animate-spin" : ""}`}
            />
          </button>
        </div>
      </div>
      {period === "custom" && (
        <div
          className="mb-3 flex flex-col gap-2"
          data-testid="warm-custom-range"
        >
          <div className="flex flex-wrap items-center gap-3">
            <label className="flex items-center gap-1 text-xs text-gray-600">
              開始
              <input
                data-testid="warm-custom-start"
                aria-label="開始日時"
                type="datetime-local"
                value={customStart}
                max={nowMax}
                onChange={(e) => onCustomStartChange?.(e.target.value)}
                className="rounded border border-gray-300 px-2 py-1 text-sm"
              />
            </label>
            <label className="flex items-center gap-1 text-xs text-gray-600">
              終了
              <input
                data-testid="warm-custom-end"
                aria-label="終了日時"
                type="datetime-local"
                value={customEnd}
                max={nowMax}
                onChange={(e) => onCustomEndChange?.(e.target.value)}
                className="rounded border border-gray-300 px-2 py-1 text-sm"
              />
            </label>
          </div>
          {rangeError && (
            <InlineBanner tone="warn" testId="warm-range-error">
              {rangeError}
            </InlineBanner>
          )}
        </div>
      )}
      <div className="h-[400px]">
        {chartData.length === 0 && !warmLoading ? (
          <div className="flex items-center justify-center h-full text-gray-600 text-sm">
            データがありません
          </div>
        ) : (
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={chartData}>
              <CartesianGrid strokeDasharray="3 3" />
              <XAxis
                dataKey="time"
                interval="preserveStartEnd"
                minTickGap={50}
              />
              <YAxis
                domain={["auto", "auto"]}
                label={
                  unit
                    ? {
                        value: unit,
                        angle: -90,
                        position: "insideLeft",
                        style: { fontSize: 12 },
                      }
                    : undefined
                }
              />
              <Tooltip content={makeTooltip(unit)} />
              <Line
                type="monotone"
                dataKey="value"
                stroke={SERIES_COLOR}
                dot={false}
              />
            </LineChart>
          </ResponsiveContainer>
        )}
      </div>
    </div>
  );
}
