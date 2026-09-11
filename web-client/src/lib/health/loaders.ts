/**
 * `/health`（データ品質, #453）のデータアクセス継ぎ目。
 *
 * 画面が必要とする読み取りをすべて注入可能な async ローダとして表現しているので、
 * ビューはオフラインで単体テストできる（`lib/home/loaders.ts` と同じ方針）。
 * 本番の結線は下の {@link productionHealthLoaders}。
 */

import { fetchGateways } from "@/lib/admin/gateways";
import { listBuildings, listFloors } from "@/lib/resources/repository";
import type { ResourceRef } from "@/lib/resources/types";
import type { HealthQuery } from "./query";
import {
  fetchPointHealth,
  fetchPointHealthSummary,
  type HealthPage,
  type HealthSummary,
} from "./repository";

export type HealthLoaders = {
  loadHealth: (query: HealthQuery) => Promise<HealthPage>;
  loadSummary: (query: HealthQuery) => Promise<HealthSummary>;
  loadBuildings: () => Promise<ResourceRef[]>;
  loadFloors: (buildingDtId: string) => Promise<ResourceRef[]>;
  /**
   * 絞り込み用の gateway ID 一覧。管理者向けの一覧 API しか無いので、権限が無ければ空を返す
   * （ビュー側は表示中の行が持つ gateway ID で補うので、operator でも実用的に絞り込める）。
   */
  loadGatewayIds: () => Promise<string[]>;
};

export const productionHealthLoaders: HealthLoaders = {
  loadHealth: (query) => fetchPointHealth(query),
  loadSummary: (query) => fetchPointHealthSummary(query),
  loadBuildings: () => listBuildings(),
  loadFloors: (buildingDtId) => listFloors(buildingDtId),
  loadGatewayIds: async () => {
    // 一覧そのものは画面の主目的ではないので、失敗しても絞り込みの選択肢が減るだけにする。
    const gateways = await fetchGateways().catch(() => []);
    return gateways.map((g) => g.gatewayId).filter((id) => id !== "");
  },
};
