/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** 検索語（名前・ID の部分一致、大文字小文字を無視） */
      q?: string | undefined;
      /** リソース種別で絞り込み（building/floor/space/device/point）。省略時は全種別 */
      type?: string | undefined;
      /** 建物 dtId でスコープ（building/floor/space のみ対象） */
      buildingId?: string | undefined;
      /** SBCO customTags のキーで絞り込み（customTags[key] == true）。複数指定は AND（#332） */
      tag?: string[] | undefined;
      /** 最大件数（1..200、既定 50） */
      limit?: number | undefined;
      /** オフセット（既定 0） */
      offset?: number | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.ResourceSearchHit[];
  };
}>;
