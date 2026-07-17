import { ValidTelemetryData } from "@/lib/infra/aspida-client/generated/@types";
import {
  GRANULARITY_OPTIONS,
  PERIOD_PRESETS,
  spansMultipleDays,
  type GranularityOption,
  type PeriodPreset,
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
  return function CustomTooltip({ active, payload }: TooltipProps<number, string>) {
    if (active && payload && payload.length) {
      return (
        <div className="bg-white p-2 border border-gray-200 shadow-sm">
          <p className="text-sm text-gray-600">{payload[0].payload.fullDatetime}</p>
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
}: {
  warmData: ValidTelemetryData[];
  warmLoading: boolean;
  onRefresh: () => void;
  period: PeriodPreset;
  granularity: GranularityOption;
  onPeriodChange: (period: PeriodPreset) => void;
  onGranularityChange: (granularity: GranularityOption) => void;
  unit?: string;
}) {
  const multiDay = spansMultipleDays(period);
  const chartData = [...warmData]
    .sort(
      (a, b) =>
        new Date(a.datetime || "").getTime() - new Date(b.datetime || "").getTime(),
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
              onChange={(e) => onPeriodChange(e.target.value as PeriodPreset)}
              className="rounded border border-gray-300 px-2 py-1 text-sm"
            >
              {PERIOD_PRESETS.map((p) => (
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
              onChange={(e) => onGranularityChange(e.target.value as GranularityOption)}
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
            <ArrowPathIcon className={`h-5 w-5 ${warmLoading ? "animate-spin" : ""}`} />
          </button>
        </div>
      </div>
      <div className="h-[400px]">
        {chartData.length === 0 && !warmLoading ? (
          <div className="flex items-center justify-center h-full text-gray-400 text-sm">
            データがありません
          </div>
        ) : (
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={chartData}>
              <CartesianGrid strokeDasharray="3 3" />
              <XAxis dataKey="time" interval="preserveStartEnd" minTickGap={50} />
              <YAxis
                domain={["auto", "auto"]}
                label={
                  unit
                    ? { value: unit, angle: -90, position: "insideLeft", style: { fontSize: 12 } }
                    : undefined
                }
              />
              <Tooltip content={makeTooltip(unit)} />
              <Line type="monotone" dataKey="value" stroke={SERIES_COLOR} dot={false} />
            </LineChart>
          </ResponsiveContainer>
        )}
      </div>
    </div>
  );
}
