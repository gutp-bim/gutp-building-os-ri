/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  post: {
    status: 200;

    /** OK */
    resBody: {
      [key: string]: Types.PermissionsControllerResolvedPermissionInfo;
    };

    reqBody: string[];
  };
}>;
