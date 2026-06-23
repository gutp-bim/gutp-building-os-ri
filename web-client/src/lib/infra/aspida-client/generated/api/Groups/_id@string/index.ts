/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  get: {
    status: 200;
    /** OK */
    resBody: Types.GroupDetailResponse;
  };

  put: {
    status: 204;
    reqBody: Types.UpdateGroupRequest;
  };

  delete: {
    status: 204;
  };
}>;
