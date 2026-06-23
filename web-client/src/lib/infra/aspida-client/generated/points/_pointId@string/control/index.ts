/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  post: {
    status: 200;
    /** OK */
    resBody: Types.PointControlResponse;
    /** 制御に関する情報 */
    reqBody: Types.PointControlRequest;
  };
}>;
