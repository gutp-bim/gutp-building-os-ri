/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      subjectType?: string | undefined;
      targetId?: string | undefined;
      limit?: number | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.AdminAuditResponse[];
  };
}>;
