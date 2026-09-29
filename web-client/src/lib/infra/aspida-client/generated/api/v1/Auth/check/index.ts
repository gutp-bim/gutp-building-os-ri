/* eslint-disable */
import type { DefineMethods } from 'aspida';

export type Methods = DefineMethods<{
  get: {
    query?: {
      resourceType?: string | undefined;
      resourceId?: string | undefined;
      action?: string | undefined;
    } | undefined;

    status: 200;
  };
}>;
