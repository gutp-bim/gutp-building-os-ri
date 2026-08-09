import type { PointDetail } from "@/lib/infra/aspida-client/generated/@types";

/** アナログ制御の入力範囲。境界が不明なときは `undefined`（＝制限なし）。 */
export type ControlRange = { min?: number; max?: number };

/**
 * アナログ制御の入力範囲を解決する。
 *
 * **制御範囲の正は ControlSchema (`bos:minValue` / `bos:maxValue`)** — サーバ側の入力値検証
 * (`ControlValueValidator`) が見ているのもこれなので、UI が別の範囲を出すと画面上は有効な値が
 * サーバで弾かれる。
 *
 * `point.minPresValue` / `maxPresValue` (`sbco:minPresValue` / `sbco:maxPresValue`) は BACnet
 * オブジェクトの raw レンジであって制御書き込み範囲ではない。ControlSchema がその境界を持たない
 * 点に対する**表示上のフォールバック**としてのみ使う（境界ごとに独立してフォールバックする。
 * ControlSchema が上限だけを定義していれば下限は raw レンジから補う）。
 *
 * どちらも無い場合は範囲**未知**を返す。以前は `?? 0` / `?? 100` に落としていたため、
 * twin に正しい範囲があっても・無くても常に 0〜100 と表示して送信していた (#298)。
 */
export const resolveControlRange = (
  pointDetail: PointDetail,
): ControlRange => ({
  min:
    pointDetail.controlSchema?.minValue ??
    pointDetail.point.minPresValue ??
    undefined,
  max:
    pointDetail.controlSchema?.maxValue ??
    pointDetail.point.maxPresValue ??
    undefined,
});

/** 範囲ラベル。片側だけ分かっている場合も、分かっている側だけを見せる。 */
export const controlRangeLabel = ({
  min,
  max,
}: ControlRange): string | null => {
  if (min != null && max != null) return `${min}～${max}`;
  if (min != null) return `${min} 以上`;
  if (max != null) return `${max} 以下`;
  return null;
};
