/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /**
       * `hash`（既定。従来どおり）または `original`（`resources` は元の業務 ID だけ。
       *             元 ID が分からない権限は `unresolved` にハッシュで分けて返す。#504）。admin は常に
       *             `resources: null`（全件）。
       */
      idFormat?: string | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.MyResourcesResponse;
  };
}>;
