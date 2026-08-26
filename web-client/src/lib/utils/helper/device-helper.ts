export const toDisplayDeviceType = (deviceTypeString: string) => {
  const split = deviceTypeString.split(":");
  if (split.length < 4) return deviceTypeString;
  return split[3].split(";")[0];
};

/**
 * Structural parameter, not the aspida `Point` (#350): these helpers need only a handful of fields, and typing
 * it on just those lets it accept both the generated wire type and the domain `PointResource` while
 * the UI migrates off aspida. It also keeps this file — which lives in `src/lib` and is therefore
 * invisible to the ESLint façade guard — from being the one place that quietly re-imports the wire
 * types after the UI has stopped.
 */
type PointAddressing = {
  objectTypeBacnet?: string | null;
  instanceNoBacnet?: number | null;
  deviceIdBacnet?: string | null;
  /** twin の `bos:protocol`。明示されていればアドレッシングの形より優先する。 */
  protocol?: string | null;
  /** twin の `sbco:localId`（BACnet の ObjectID / MQTT の TOPIC / OPC-UA の nodeId）。 */
  localId?: string | null;
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
export const getCollectionProtocol = (
  point: PointAddressing | undefined,
): string | null => {
  if (!point) return null;

  if (point.protocol) {
    const normalized = point.protocol.toLowerCase();
    if (normalized === "mqtt") return "MQTT";
    if (normalized === "opcua" || normalized === "opc-ua") return "OPC-UA";
    if (normalized === "bacnet") return "BACnet";
    return point.protocol;
  }

  if (
    point.objectTypeBacnet != null ||
    point.instanceNoBacnet != null ||
    point.deviceIdBacnet != null
  ) {
    return "BACnet";
  }

  return null;
};

export const getPointLocalId = (
  point: PointAddressing | undefined,
): string | null => point?.localId || null;
