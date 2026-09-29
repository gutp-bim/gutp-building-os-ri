/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../@types';

export type Methods = DefineMethods<{
  /**
   * The 200 response is BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListResponse for the full list (no `since`, or
   * snapshot evicted) and BuildingOs.ApiServer.GatewayProvisioning.GatewayPointListDiffResponse for a resolvable `?since=`
   * diff. Swagger documents only the full-list shape (Swashbuckle doesn't merge two response types
   * under one status code without a custom schema filter) — treat it as the primary contract.
   */
  get: {
    query?: {
      since?: string | undefined;
    } | undefined;

    status: 200;
    /** OK */
    resBody: Types.GatewayPointListResponse;
  };
}>;
