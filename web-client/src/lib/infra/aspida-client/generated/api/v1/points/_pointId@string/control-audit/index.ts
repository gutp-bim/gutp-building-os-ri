/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** 1 ページの件数（1〜200、既定 50） */
      limit?: number | undefined;
      /** この時刻以降（含む）の行だけを返す */
      start?: string | undefined;
      /** この時刻より前（含まない）の行だけを返す */
      end?: string | undefined;
      /** 前ページの `X-Next-Cursor` の値 */
      cursor?: string | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.PointControlAuditResponse[];
  };
}>;
