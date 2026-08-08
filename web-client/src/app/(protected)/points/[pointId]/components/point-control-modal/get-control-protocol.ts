import type { PointDetail } from "@/lib/infra/aspida-client/generated/@types";
import { getCollectionProtocol } from "@/lib/utils/helper/device-helper";

/**
 * 制御プロトコルを判定する
 * BACnet: Point に BACnet固有フィールドが存在する（Kandt ゲートウェイ経由の場合も下流は BACnet）
 *
 * 判定そのものは `getCollectionProtocol` に委譲する。かつてこの判定は 2 箇所で別々に実装されており
 * （こちらは 3 条件、`device-helper` 側は `deviceIdBacnet` のみ）、同じ画面で結論が食い違っていた (#294)。
 * 制御に使うプロトコルは収集プロトコルと同一なので、判定は 1 つで足りる。
 * 制御が可能かどうかはプロトコルではなく `point.writable` が決める点に注意。
 */
export const getControlProtocol = (
  pointDetail: PointDetail,
): "BACnet" | null => getCollectionProtocol(pointDetail.point);
