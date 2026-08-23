
export const toDisplayDeviceType = (deviceTypeString: string) => {
  const split = deviceTypeString.split(":");
  if (split.length < 4) return deviceTypeString;
  return split[3].split(";")[0];
};

/**
 * ポイントの「収集プロトコル」を判定する。
 *
 * BACnet 固有フィールド（objectType / instanceNo / deviceId）が示すのは *アドレッシング* であって
 * 制御可否ではない。制御できるかどうかは `point.writable`（および ControlSchema）が決める。
 * 両者を 1 つの「制御タイプ」に畳んでいたため、read-only な BACnet ポイントが
 * 「BACnet で収集している」ことすら表示できなかった (#294)。
 *
 * `instanceNoBacnet` は 0 も有効な値なので、truthy ではなく `!= null` で判定する。
 * この述語がリポジトリ内で唯一の BACnet 判定であること — 以前は同じ画面で
 * 1 条件版と 3 条件版の 2 実装が併存していた。
 */
/**
 * Structural parameter, not the aspida `Point` (#350): this predicate needs three fields, and typing
 * it on just those lets it accept both the generated wire type and the domain `PointResource` while
 * the UI migrates off aspida. It also keeps this file — which lives in `src/lib` and is therefore
 * invisible to the ESLint façade guard — from being the one place that quietly re-imports the wire
 * types after the UI has stopped.
 */
type BacnetAddressed = {
  objectTypeBacnet?: string | null;
  instanceNoBacnet?: number | null;
  deviceIdBacnet?: string | null;
};

export const getCollectionProtocol = (
  point: BacnetAddressed | undefined,
): "BACnet" | null => {
  if (!point) return null;

  if (
    point.objectTypeBacnet != null ||
    point.instanceNoBacnet != null ||
    point.deviceIdBacnet != null
  ) {
    return "BACnet";
  }

  return null;
};
