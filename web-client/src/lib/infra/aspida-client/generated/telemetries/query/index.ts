/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      pointId?: string | undefined;
      start?: string | undefined;
      end?: string | undefined;
      granularity?: string | undefined;
      latest?: boolean | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.ValidTelemetryData[];
  };
}>;
