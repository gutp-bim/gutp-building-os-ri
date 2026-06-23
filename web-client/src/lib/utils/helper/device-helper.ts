import { Device, Point } from "@/lib/infra/aspida-client/generated/@types";

export const toDisplayDeviceType = (deviceTypeString: string) => {
  const split = deviceTypeString.split(":");
  if (split.length < 4) return deviceTypeString;
  return split[3].split(";")[0];
};

/**
 * デバイスから制御タイプを判定する
 * @param device デバイス情報
 * @param point
 * @returns "BACnet" | "DkConnect" | null
 */
export const getControlType = (
  device: Device | undefined,
  point: Point | undefined,
): "BACnet" | "DkConnect" | null => {
  if (!device || !point) return null;

  // DK Connect判定ロジック（デバイスタイプに"DkConnect"または"Daikin"が含まれる）
  if (device.gatewayId) {
    const deviceType = device.gatewayId.toLowerCase();
    if (deviceType.includes("dkapi") || deviceType.includes("daikin")) {
      return "DkConnect";
    }
  }

  // BACnet判定ロジック（gatewayIdが存在する）
  if (point.deviceIdBacnet != null) {
    return "BACnet";
  }

  return null;
};
