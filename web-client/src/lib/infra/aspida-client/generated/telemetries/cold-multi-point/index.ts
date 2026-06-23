/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      pointIds?: string[] | undefined;
      startTime?: string | undefined;
      endTime?: string | undefined;
    } | undefined;

    status: 200;

    /** OK */
    resBody: {
      [key: string]: Types.ValidTelemetryData[];
    };
  };
}>;
