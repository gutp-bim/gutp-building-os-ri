/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  get: {
    status: 200;
    /** OK */
    resBody: Types.ResourceMetadataResponse;
  };

  patch: {
    status: 204;
    reqBody: Types.ResourceMetadataPatchRequest;
  };
}>;
