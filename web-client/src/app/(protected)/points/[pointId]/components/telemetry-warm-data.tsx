import { ValidTelemetryData } from "@/lib/infra/aspida-client/generated/@types";
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

const formatTime = (datetime: string) => {
  const date = new Date(datetime);
  return `${date.getHours().toString().padStart(2, "0")}:${
    date.getMinutes().toString().padStart(2, "0")
  }`;
};

const CustomTooltip = ({ active, payload }: TooltipProps<number, string>) => {
  if (active && payload && payload.length) {
    return (
      <div className="bg-white p-2 border border-gray-200 shadow-sm">
        <p className="text-sm text-gray-600">
          {payload[0].payload.fullDatetime}
        </p>
        <p className="text-sm font-semibold">
          {`値: ${payload[0].value?.toFixed(1)}`}
        </p>
      </div>
    );
  }
  return null;
};

export function TelemetryWarmData({
  warmData,
  warmLoading,
  onRefresh,
}: {
  warmData: ValidTelemetryData[];
  warmLoading: boolean;
  onRefresh: () => void;
}) {
  const chartData = [...warmData]
    .sort(
      (a, b) =>
        new Date(a.datetime || "").getTime() -
        new Date(b.datetime || "").getTime(),
    )
    .map((data) => ({
      time: data.datetime ? formatTime(data.datetime) : "",
      value: data.value,
      fullDatetime: data.datetime
        ? new Date(data.datetime).toLocaleString("ja-JP")
        : "",
    }));

  return (
    <div className="bg-white p-4 rounded-lg shadow">
      <div className="flex items-center justify-between mb-2">
        <h3 className="text-sm font-medium text-gray-700">過去 24 時間</h3>
        <button
          onClick={onRefresh}
          disabled={warmLoading}
          className="text-gray-600 hover:text-gray-900 disabled:opacity-50"
        >
          <ArrowPathIcon
            className={`h-5 w-5 ${warmLoading ? "animate-spin" : ""}`}
          />
        </button>
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
              <XAxis
                dataKey="time"
                interval="preserveStartEnd"
                minTickGap={50}
              />
              <YAxis domain={["auto", "auto"]} />
              <Tooltip content={<CustomTooltip />} />
              <Line
                type="monotone"
                dataKey="value"
                stroke="#8884d8"
                dot={false}
              />
            </LineChart>
          </ResponsiveContainer>
        )}
      </div>
    </div>
  );
}
