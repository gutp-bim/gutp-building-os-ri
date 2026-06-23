import { ValidTelemetryData } from "@/lib/infra/aspida-client/generated/@types";
import { unitLabelMap } from "@/lib/utils/helper/telemetry-helper";
import { ArrowPathIcon } from "@heroicons/react/24/outline";
import { useMemo } from "react";

export function TelemetryHotData({
  hotData,
  hotLoading,
  onRefresh,
  onDownloadClick,
  scale = 1,
  unit,
  labels,
}: {
  hotData: ValidTelemetryData | null;
  hotLoading: boolean;
  onRefresh: () => void;
  onDownloadClick: () => void;
  scale?: number;
  unit?: string;
  labels?: string;
}) {
  const splitLabels = labels ? labels.split(",") : null;

  const displayHotData = useMemo(() => {
    if (!hotData || hotData.value === null || hotData.value === undefined)
      return "-";

    if (splitLabels && splitLabels.length > 0) {
      return splitLabels[hotData.value - 1];
    }

    return `${hotData.value * scale} ${unit ? (unitLabelMap[unit] ?? unit) : ""}`;
  }, [hotData, scale, splitLabels, unit]);

  return (
    <div className={"flex flex-col gap-2 grow-4"}>
      <div className={"flex justify-center"}>
        <div className="w-[300px] bg-white rounded-lg shadow relative flex items-center justify-center py-12">
          <div className="absolute top-2 right-4">
            <button
              onClick={onRefresh}
              disabled={hotLoading}
              className="text-gray-600 hover:text-gray-900 disabled:opacity-50"
            >
              <ArrowPathIcon
                className={`h-5 w-5 ${hotLoading ? "animate-spin" : ""}`}
              />
            </button>
          </div>
          <div className="text-center">
            <div className="text-4xl font-bold mb-2">{displayHotData}</div>
            <div className="text-gray-500">
              {hotData?.datetime &&
                new Date(hotData.datetime).toLocaleString("ja-JP")}
            </div>
          </div>
        </div>
      </div>
      <div className="mt-4 flex justify-end">
        <button
          onClick={onDownloadClick}
          className="bg-blue-500 hover:bg-blue-600 text-white px-4 py-2 rounded-md transition-colors"
        >
          ダウンロード
        </button>
      </div>
    </div>
  );
}
