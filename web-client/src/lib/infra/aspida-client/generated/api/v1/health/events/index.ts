/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query?: {
      /** open（未解消）| cleared（解消済み）。省略時は両方 */
      lifecycle?: string | undefined;
      /** acked（確認済み）| unacked（未確認）。省略時は両方。lifecycle とは独立 */
      ack?: string | undefined;
      /** stale | missing | alarm | gateway_offline。複数指定は OR */
      kind?: string[] | undefined;
      /** point | gateway */
      subjectType?: string | undefined;
      /** pointId / gatewayId（完全一致） */
      subjectId?: string | undefined;
      /** この時刻（ISO-8601）以降に発生したものだけ */
      since?: string | undefined;
      /** 最大件数（1..500、既定 100） */
      limit?: number | undefined;
      /** オフセット（既定 0） */
      offset?: number | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.HealthEventListResponse;
  };
}>;
