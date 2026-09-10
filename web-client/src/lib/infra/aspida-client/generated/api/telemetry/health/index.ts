/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** 建物 dtId でスコープ。省略時は認可された全建物 */
      buildingDtId?: string | undefined;
      /** 階 dtId で絞り込み（完全一致） */
      floorDtId?: string | undefined;
      /** 機器 dtId で絞り込み（完全一致） */
      deviceDtId?: string | undefined;
      /** gateway ID で絞り込み（大小無視） */
      gatewayId?: string | undefined;
      /** 鮮度で絞り込み（Fresh/Stale/Missing/Unknown）。複数指定は OR、未知の値は無視 */
      freshness?: string[] | undefined;
      /** 警報で絞り込み（Normal/Warn/Critical/Unknown/Suppressed）。複数指定は OR */
      alarm?: string[] | undefined;
      /** 総合ステータスで絞り込み。複数指定は OR */
      healthStatus?: string[] | undefined;
      /** この秒数以上受信が無いものだけ。**未受信の Point は齢 ∞ として必ず含む** */
      olderThan?: number | undefined;
      /** customTags で絞り込み。複数指定は AND、大小無視 */
      tag?: string[] | undefined;
      /** pointId / 名前の部分一致（大小無視） */
      q?: string | undefined;
      /** 並べ替え（worst（既定）/ lastSeen / name） */
      sort?: string | undefined;
      /** 最大件数（1..500、既定 100） */
      limit?: number | undefined;
      /** オフセット（既定 0） */
      offset?: number | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.PointHealthListResponse;
  };
}>;
