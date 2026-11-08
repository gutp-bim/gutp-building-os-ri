import type { PointDetailResource } from "@/lib/resources/types";
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
  pointDetail: PointDetailResource,
): "BACnet" | null =>
  // getCollectionProtocol は #320 以降 MQTT / OPC-UA も返す。ここは BACnet 制御フローの
  // 入口なので、BACnet 以外は null に潰して「制御できない」に倒す。
  getCollectionProtocol(pointDetail.point) === "BACnet" ? "BACnet" : null;

/** The control modals that exist (#638). */
export type ControlModalKind = "number" | "boolean" | "enum";

/**
 * Which control modal this point opens, or null when it cannot be controlled from this screen (#638). The button
 * is offered only when one of these is true, so it never opens nothing or sends a write the server will refuse:
 *
 * - the point goes through the BACnet control flow (the only one with modals);
 * - it is not `writable: false` — the server refuses exactly that (`ControlTypeResolver`), an unset value passes;
 * - it has a ControlSchema whose `dataType`, spelled as the server reads it (trimmed, case-insensitive,
 *   `ControlValueValidator`), is one of the three types a modal exists for.
 */
export const getControlModalKind = (
  pointDetail: PointDetailResource,
): ControlModalKind | null => {
  if (getControlProtocol(pointDetail) === null) return null;
  if (pointDetail.point.writable === false) return null;
  const type = pointDetail.controlSchema?.dataType?.trim().toLowerCase();
  return type === "number" || type === "boolean" || type === "enum"
    ? type
    : null;
};
