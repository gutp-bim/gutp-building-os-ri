import { Point } from "@/lib/infra/aspida-client/generated/@types";

export const toDisplayDeviceType = (deviceTypeString: string) => {
  const split = deviceTypeString.split(":");
  if (split.length < 4) return deviceTypeString;
  return split[3].split(";")[0];
};

/**
 * ポイントから制御タイプを判定する
 * @param point
 * @returns "BACnet" | null
 */
export const getControlType = (
  point: Point | undefined,
): "BACnet" | null => {
  if (!point) return null;

  // BACnet判定ロジック（point に BACnet 固有フィールドが存在する）
  if (point.deviceIdBacnet != null) {
    return "BACnet";
  }

  return null;
};
