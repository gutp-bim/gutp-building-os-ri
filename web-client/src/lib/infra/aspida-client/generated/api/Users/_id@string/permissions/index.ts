/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  post: {
    status: 200;
    /** OK */
    resBody: Types.UsersControllerUserResponse;
    reqBody: Types.UsersControllerAddPermissionRequest;
  };

  delete: {
    status: 200;
    /** OK */
    resBody: Types.UsersControllerUserResponse;
    reqBody: Types.UsersControllerRemovePermissionRequest;
  };
}>;
