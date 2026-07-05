/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../@types';

export type Methods = DefineMethods<{
  get: {
    status: 200;
    /** OK */
    resBody: Types.GroupsControllerGroupResponse[];
  };

  post: {
    status: 201;
    /** Created */
    resBody: Types.GroupsControllerGroupResponse;
    reqBody: Types.GroupsControllerCreateGroupRequest;
  };
}>;
