/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** building / floor / space / device / point */
      resourceType?: string | undefined;
      /** read / write など */
      action?: string | undefined;
      /**
       * `hash`（既定。従来どおり、逆引きできない ID はハッシュのまま混在）または `original`
       *             （元の業務 ID だけを `accessibleResourceIds` に返し、元 ID が分からないものは
       *             `unresolvedResourceIds` にハッシュで分けて返す。#504）。
       */
      idFormat?: string | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.AccessibleResourcesResponse;
  };
}>;
