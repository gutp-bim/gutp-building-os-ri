/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** 必須. ポイントID */
      pointId?: string | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.ValidTelemetryData[];
  };
}>;
