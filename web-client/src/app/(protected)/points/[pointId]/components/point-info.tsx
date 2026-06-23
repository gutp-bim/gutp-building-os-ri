import { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { getControlType } from "@/lib/utils/helper/device-helper";
import { Dialog } from "@headlessui/react";
import { ClipboardIcon } from "@heroicons/react/24/outline";
import { useState } from "react";

const BACNET_OBJECT_TYPE_MAP: Record<string, string> = {
  0: "AnalogInput",
  1: "AnalogOutput",
  2: "AnalogValue",
  3: "BinaryInput",
  4: "BinaryOutput",
  5: "BinaryValue",
  13: "MultiStateInput",
  14: "MultiStateOutput",
  19: "MultiStateValue",
  23: "Accumulator",
};

export function PointInfo({ pointDetail }: { pointDetail: PointDetail }) {
  const [isJsonModalOpen, setIsJsonModalOpen] = useState(false);
  const controlType = getControlType(pointDetail.device, pointDetail.point);

  return (
    <div className="bg-white p-6 rounded-xl shadow w-full mx-auto">
      <h2 className="text-2xl font-bold mb-6">{pointDetail.point.name}</h2>
      <div className="border-b border-gray-500 pb-4 mb-4">
        <div className="grid grid-cols-2 gap-y-2 text-sm">
          <div className="font-semibold">ビル</div>
          <div>{pointDetail.device?.buildingName || "-"}</div>
          <div className="font-semibold">フロア</div>
          <div>{pointDetail.floor?.name || "-"}</div>
        </div>
      </div>
      <div className="border-b border-gray-500 pb-4 mb-4">
        <div className="grid grid-cols-2 gap-y-2 text-sm">
          <div className="font-semibold">スペース</div>
          <div>{pointDetail.space?.name || "-"}</div>
        </div>
      </div>
      <div className="border-b border-gray-500 pb-4 mb-4">
        <div className="grid grid-cols-2 gap-y-2 text-sm">
          <div className="font-semibold">Point ID</div>
          <div>{pointDetail.point.id}</div>
          <div className="font-semibold">ポイント種別</div>
          <div>{pointDetail.point.type || "-"}</div>
          <div className="font-semibold">ポイント区分</div>
          <div>{pointDetail.point.specification || "-"}</div>
          <div className="font-semibold">書き込み可否</div>
          <div>{pointDetail.point.writable ? "可" : "不可"}</div>
          <div className="font-semibold">制御タイプ</div>
          <div>
            {controlType ? (
              <span
                className={`inline-flex items-center px-2 py-1 rounded text-xs font-medium ${
                  controlType === "BACnet"
                    ? "bg-blue-100 text-blue-800"
                    : "bg-green-100 text-green-800"
                }`}
              >
                {controlType}
              </span>
            ) : (
              "-"
            )}
          </div>
        </div>
      </div>

      {/* BACnet情報 */}
      {controlType === "BACnet" && (
        <div className="pb-2">
          <div className="bg-blue-50 p-3 rounded mb-2">
            <h3 className="font-semibold text-sm text-blue-800 mb-2">
              BACnet 情報
            </h3>
            <div className="grid grid-cols-2 gap-y-2 text-sm">
              <div className="font-semibold text-blue-700">
                Object Type Bacnet
              </div>
              <div className="text-blue-900">
                {pointDetail.point.objectTypeBacnet
                  ? (BACNET_OBJECT_TYPE_MAP[
                      pointDetail.point.objectTypeBacnet
                    ] ?? pointDetail.point.objectTypeBacnet)
                  : "-"}
              </div>
              <div className="font-semibold text-blue-700">
                Device ID Bacnet
              </div>
              <div className="text-blue-900">
                {pointDetail.point.deviceIdBacnet || "-"}
              </div>
              <div className="font-semibold text-blue-700">
                Instance No Bacnet
              </div>
              <div className="text-blue-900">
                {pointDetail.point.instanceNoBacnet || "-"}
              </div>
            </div>
          </div>
        </div>
      )}

      <div className="mt-2">
        {pointDetail.point.rowDataString ? (
          <button
            onClick={() => setIsJsonModalOpen(true)}
            className="text-blue-600 hover:text-blue-800 text-sm cursor-pointer font-medium"
          >
            元データを表示
          </button>
        ) : (
          <span className="text-gray-400 text-sm">元データなし</span>
        )}
      </div>

      {/* JSONデータ表示モーダル */}
      {pointDetail.point.rowDataString && (
        <Dialog
          open={isJsonModalOpen}
          onClose={() => setIsJsonModalOpen(false)}
          className="relative z-50"
        >
          <div className="fixed inset-0 bg-black/30" aria-hidden="true" />
          <div className="fixed inset-0 flex items-center justify-center p-4">
            <Dialog.Panel className="bg-white rounded-lg p-6 w-full max-w-2xl">
              <Dialog.Title className="text-lg font-medium mb-4">
                元データ (Row Data)
              </Dialog.Title>
              <div className="relative">
                <pre className="bg-gray-100 p-4 rounded-md overflow-auto max-h-[60vh] text-sm">
                  {(() => {
                    try {
                      const jsonData = JSON.parse(
                        pointDetail.point.rowDataString!,
                      );
                      return JSON.stringify(jsonData, null, 2);
                    } catch {
                      return pointDetail.point.rowDataString!;
                    }
                  })()}
                </pre>
                <button
                  onClick={() => {
                    navigator.clipboard.writeText(
                      pointDetail.point.rowDataString!,
                    );
                  }}
                  className="absolute top-2 right-2 p-2 text-gray-600 hover:text-gray-900 bg-white rounded-md shadow-sm cursor-pointer"
                  title="クリップボードにコピー"
                >
                  <ClipboardIcon className="h-5 w-5" />
                </button>
              </div>
              <div className="mt-4 flex justify-end">
                <button
                  onClick={() => setIsJsonModalOpen(false)}
                  className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 rounded-md hover:bg-gray-200 cursor-pointer"
                >
                  閉じる
                </button>
              </div>
            </Dialog.Panel>
          </div>
        </Dialog>
      )}
    </div>
  );
}
