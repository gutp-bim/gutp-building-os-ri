/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** 必須. ポイントID */
      pointId?: string | undefined;
      /** 開始時刻（latest=true の場合は不要） */
      start?: string | undefined;
      /** 終了時刻（latest=true の場合は不要） */
      end?: string | undefined;
      /** 集計粒度: raw / hour / day（省略時: raw） */
      granularity?: Types.TelemetryGranularity | undefined;
      /** true の場合は最新値のみ返す */
      latest?: boolean | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.ValidTelemetryData[];
  };
}>;
