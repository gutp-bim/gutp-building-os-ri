import type { PointDetailResource } from "@/lib/resources/types";
import { getCollectionProtocol } from "@/lib/utils/helper/device-helper";

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

export function PointInfo({
  pointDetail,
}: {
  pointDetail: PointDetailResource;
}) {
  // 収集プロトコル（アドレッシング）と書き込み可否（制御できるか）は別の問い。以前は
  // 「制御タイプ」1 行に畳んでいたため、read-only な BACnet ポイントは収集経路も空欄になった (#294)。
  const collectionProtocol = getCollectionProtocol(pointDetail.point);
  // 階層が欠けているのは「値が無い」ではなく「まだ割り当てられていない」状態。"-" だと
  // データ不足と表示不具合の区別がつかない (#294)。
  const unassigned = "未割当";

  return (
    <div className="bg-white p-6 rounded-xl shadow w-full mx-auto">
      <h2 className="text-2xl font-bold mb-6">{pointDetail.point.name}</h2>
      <div className="border-b border-gray-200 pb-4 mb-4">
        <div className="grid grid-cols-2 gap-y-2 text-sm">
          <div className="font-semibold">ビル</div>
          <div>{pointDetail.device?.buildingName || unassigned}</div>
          <div className="font-semibold">フロア</div>
          <div>{pointDetail.floor?.name || unassigned}</div>
        </div>
      </div>
      <div className="border-b border-gray-200 pb-4 mb-4">
        <div className="grid grid-cols-2 gap-y-2 text-sm">
          <div className="font-semibold">スペース</div>
          <div>{pointDetail.space?.name || unassigned}</div>
        </div>
      </div>
      <div className="border-b border-gray-200 pb-4 mb-4">
        <div className="grid grid-cols-2 gap-y-2 text-sm">
          <div className="font-semibold">Point ID</div>
          <div>{pointDetail.point.id}</div>
          <div className="font-semibold">ポイント種別</div>
          <div>{pointDetail.point.kind || "-"}</div>
          <div className="font-semibold">ポイント区分</div>
          <div>{pointDetail.point.specification || "-"}</div>
          <div className="font-semibold">書き込み可否</div>
          {/* writable が null（twin に未設定）を "不可" と同じに描くと、読み取り専用と
              メタデータ欠落が同じ表示になってしまう。 */}
          <div>
            {pointDetail.point.writable == null
              ? unassigned
              : pointDetail.point.writable
                ? "可"
                : "不可（読み取り専用）"}
          </div>
          <div className="font-semibold">収集プロトコル</div>
          <div>
            {collectionProtocol ? (
              <span className="inline-flex items-center px-2 py-1 rounded text-xs font-medium bg-blue-100 text-blue-800">
                {collectionProtocol}
              </span>
            ) : (
              "-"
            )}
          </div>
        </div>
      </div>

      {/* BACnet情報 */}
      {collectionProtocol === "BACnet" && (
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
                {pointDetail.point.instanceNoBacnet ?? "-"}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
