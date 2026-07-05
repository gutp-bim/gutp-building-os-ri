import type { PointDetail } from "@/lib/infra/aspida-client/generated/@types";

/**
 * 制御プロトコルを判定する
 * BACnet: Point に BACnet固有フィールドが存在する（Kandt ゲートウェイ経由の場合も下流は BACnet）
 */
export const getControlProtocol = (
  pointDetail: PointDetail,
): "BACnet" | null => {
  const point = pointDetail.point;

  // instanceNoBacnet は 0 も有効な値のため truthy チェックではなく != null で判定する。
  if (
    point.objectTypeBacnet != null ||
    point.instanceNoBacnet != null ||
    point.deviceIdBacnet != null
  ) {
    return "BACnet";
  }

  return null;
};
