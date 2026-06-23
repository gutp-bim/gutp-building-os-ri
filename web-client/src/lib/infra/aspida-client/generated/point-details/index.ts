/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** ビルのdtId */
      buildingDtId?: string | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.PointDetail[];
  };
}>;
