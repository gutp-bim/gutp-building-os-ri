/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../@types';

export type Methods = DefineMethods<{
  /**
   * <b>
   *   `latest=true` の意味論（#417）</b>: 返る行は「twin 上で現在アクセス可能な点の、
   *             Hot KV に保存済みの最新値」であり、「直近に取り込まれた値」ではない。両者は通常一致するが、
   *             ある点が一時的に twin から外れていた（別の import で非公開化された）期間があると、Hot KV は
   *             その間 PUT されないため最後に保存された値がそのまま残る。点が再び公開されると、この古い値が
   *             返る ── `Datetime` は再公開の瞬間より前の時刻を指しうる。この行が呼び出し時点でどれだけ
   *             古いかは BuildingOs.ApiServer.Telemetry.TelemetryReading.IngestedAt（Hot KV への書き込み時刻。`Datetime`
   *             とは別物）で判定できる。テレメトリは twin から独立した時系列の事実であり、twin から外すことは
   *             「過去の観測を消す」操作ではないため、この挙動は仕様である。
   */
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
    resBody: Types.TelemetryReading[];
  };
}>;
