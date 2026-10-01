/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** 部屋に置かれた機器（`sbco:locatedIn` がこの部屋） */
      spaceDtId?: string | undefined;
      /** 部屋を介さずフロアに直接置かれた機器（#544）。`spaceDtId` とは同時に指定できない。 */
      floorDtId?: string | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.Device[];
  };
}>;
