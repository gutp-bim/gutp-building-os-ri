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
      /**
       * true の場合は最新値のみ返す。「最新」は twin の可視性でフィルタされた最新行であり、リクエスト
       * 時刻に近いとは限らない（#417）：ある点が一時的に twin の Point List から外れ、その間も送信元は
       * publish を続けていた場合、非公開だった期間のテレメトリは保存されない（gateway が point-list miss
       * として捨てる）。その点を再度公開すると、latest=true は非公開化「前」の保存済み行を返す——
       * つまりこのレスポンスの `datetime` が取り込みより古い時刻になり得る。値の新鮮さを判定する
       * 場合は `datetime` を現在時刻と比較すること。
       */
      latest?: boolean | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.TelemetryReading[];
  };
}>;
