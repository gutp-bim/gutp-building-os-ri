/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** 検索語（名前・ID の部分一致） */
      q?: string | undefined;
      /** リソース種別で絞り込み */
      type?: string | undefined;
      /** 建物 dtId でスコープ */
      buildingId?: string | undefined;
      /** customTags のキー。複数指定は AND */
      tag?: string[] | undefined;
      /** sbco:deviceType。複数指定は OR */
      deviceType?: string[] | undefined;
      /** sbco:pointType。複数指定は OR */
      pointType?: string[] | undefined;
      /** sbco:unit。複数指定は OR */
      unit?: string[] | undefined;
      /** sbco:gatewayId。複数指定は OR */
      gatewayId?: string[] | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.ResourceFacets;
  };
}>;
