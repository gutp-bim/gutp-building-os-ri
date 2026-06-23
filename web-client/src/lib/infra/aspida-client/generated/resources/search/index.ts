/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      q?: string | undefined;
      type?: string | undefined;
      buildingId?: string | undefined;
      tag?: string[] | undefined;
      limit?: number | undefined;
      offset?: number | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.ResourceSearchHit[];
  };
}>;
