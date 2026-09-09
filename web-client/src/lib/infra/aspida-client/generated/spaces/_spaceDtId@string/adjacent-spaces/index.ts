/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  /**
   * 指定した部屋に隣接する部屋（`sbco:Room`）の一覧。隣接関係は BOT の対称関係
   * `bot:adjacentZone` に由来し、取り込み時に双方向へ正規化されている。
   * 読み取り権限のない隣室は結果から除外される。部屋自体が存在しない場合は 404。
   */
  get: {
    status: 200;
    /** OK */
    resBody: Types.Space[];
  };
}>;
