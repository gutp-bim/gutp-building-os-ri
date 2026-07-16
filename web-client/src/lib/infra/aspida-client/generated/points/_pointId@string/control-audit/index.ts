/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      limit?: number | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.PointControlAuditResponse[];
  };
}>;
