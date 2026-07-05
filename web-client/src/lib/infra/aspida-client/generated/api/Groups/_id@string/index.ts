/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  get: {
    status: 200;
    /** OK */
    resBody: Types.GroupsControllerGroupDetailResponse;
  };

  put: {
    status: 204;
    reqBody: Types.GroupsControllerUpdateGroupRequest;
  };

  delete: {
    status: 204;
  };
}>;
