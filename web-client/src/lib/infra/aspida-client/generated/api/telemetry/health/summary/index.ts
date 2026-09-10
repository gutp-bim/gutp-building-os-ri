/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

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
      /** customTags で絞り込み。複数指定は AND、大小無視 */
      tag?: string[] | undefined;
      /** pointId / 名前の部分一致（大小無視） */
      q?: string | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.PointHealthSummaryResponse;
  };
}>;
