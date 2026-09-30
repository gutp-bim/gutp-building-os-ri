/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /**
       * `hash`（既定。従来どおり）または `original`（`resources` は元の業務 ID だけ。
       *             元 ID が分からない権限は `unresolved` にハッシュで分けて返す。#504）。admin は常に
       *             `resources: null`（全件）。
       */
      idFormat?: string | undefined;
      /**
       * `descendants` を指定すると、読めるリソースの twin 上の子孫（`targetType` まで）も各種別に加える
       *             （#509）。子孫は認可の祖先判定と同じ経路で辿るので、返る ID はすべて読める。業務 ID で返すため
       *             `idFormat=original` と併用する（それ以外は 400）。`unresolved` の権限は twin 上の位置が
       *             分からないので展開しない。
       */
      expand?: string | undefined;
      /**
       * 展開する深さ（building / floor / space / device / point、既定 point）。`expand` が無ければ無視。
       * 展開後の ID が BuildingOs.ApiServer.Controllers.MyResourcesController.MaxExpandedIds を超えると 422（浅い `targetType` を指定する）。
       */
      targetType?: string | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.MyResourcesResponse;
  };
}>;
