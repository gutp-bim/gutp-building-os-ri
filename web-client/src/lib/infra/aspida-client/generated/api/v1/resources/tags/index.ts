/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** キーの前方一致。省略時は全キー */
      prefix?: string | undefined;
      /** 最大件数（1..100、既定 20） */
      limit?: number | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.ResourceTagCount[];
  };
}>;
