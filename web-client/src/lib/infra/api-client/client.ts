import { Fetcher } from "openapi-typescript-fetch";
import type { paths } from "./types";
import Cookies from "js-cookie";

// APIクライアントの作成
export const createApiClient = (baseUrl: string) => {
  const fetcher = Fetcher.for<paths>();

  // ベースURLの設定
  fetcher.configure({
    baseUrl,
    init: {
      headers: {
        "Content-Type": "application/json",
        "Authorization": `Bearer ${Cookies.get("oidc.access_token") || ""}`,
      },
    },
  });

  return {
    // Buildings
    getBuildings: fetcher.path("/buildings").method("get").create(),
    getBuilding: fetcher.path("/buildings/{buildingId}").method("get").create(),

    // Floors
    getFloors: fetcher.path("/floors").method("get").create(),
    getFloor: fetcher.path("/floors/{floorId}").method("get").create(),

    // Spaces
    getSpaces: fetcher.path("/spaces").method("get").create(),
    getSpace: fetcher.path("/spaces/{spaceId}").method("get").create(),

    // Devices
    getDevices: fetcher.path("/devices").method("get").create(),
    getDevice: fetcher.path("/devices/{deviceId}").method("get").create(),
    postDeviceCommand: fetcher.path("/devices/command/{deviceId}").method(
      "post",
    ).create(),

    // Points
    getPoints: fetcher.path("/points").method("get").create(),
    getPoint: fetcher.path("/points/{pointId}").method("get").create(),

    // Telemetries
    getTelemetries: fetcher.path("/telemetries").method("get").create(),
  };
};

// クライアントの型
export type ApiClient = ReturnType<typeof createApiClient>;

// APIクライアントのインスタンスを作成
export const apiClient = createApiClient(
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000",
);
