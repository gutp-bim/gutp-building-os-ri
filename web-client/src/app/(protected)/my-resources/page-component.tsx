"use client";

import { apiClient } from "@/lib/infra/aspida-client";
import {
  Building,
  Device,
  Floor,
  PointDetail,
  Space,
} from "@/lib/infra/aspida-client/generated/@types";
import { toDisplayDeviceType } from "@/lib/utils/helper/device-helper";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";

export default function MyResourcesPageComponent() {
  const router = useRouter();
  const [buildings, setBuildings] = useState<Building[]>([]);
  const [floors, setFloors] = useState<Floor[]>([]);
  const [spaces, setSpaces] = useState<Space[]>([]);
  const [devices, setDevices] = useState<Device[]>([]);
  const [pointDetails, setPointDetails] = useState<PointDetail[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchData = async () => {
      try {
        setLoading(true);
        const enc = encodeURIComponent;
        const client = apiClient();

        const res = await client.api.MyResources.$get();

        if (res.isAdmin) {
          router.replace("/buildings");
          return;
        }

        const resources = res.resources ?? {};
        const buildingIds = resources["building"] ?? [];
        const floorIds = resources["floor"] ?? [];
        const spaceIds = resources["space"] ?? [];
        const deviceIds = resources["device"] ?? [];
        const pointIds = resources["point"] ?? [];

        const [bldgs, flrs, spcs, devs, pts] = await Promise.all([
          Promise.all(
            buildingIds.map((id) =>
              client.buildings._buildingDtId(enc(id)).$get().catch(() => null),
            ),
          ),
          Promise.all(
            floorIds.map((id) =>
              client.floors._floorDtId(enc(id)).$get().catch(() => null),
            ),
          ),
          Promise.all(
            spaceIds.map((id) =>
              client.spaces._spaceDtId(enc(id)).$get().catch(() => null),
            ),
          ),
          Promise.all(
            deviceIds.map((id) =>
              client.devices._deviceDtId(enc(id)).$get().catch(() => null),
            ),
          ),
          Promise.all(
            pointIds.map((id) =>
              client.point_details._pointId(enc(id)).$get().catch(() => null),
            ),
          ),
        ]);

        setBuildings(bldgs.filter((b): b is Building => b !== null));
        setFloors(flrs.filter((f): f is Floor => f !== null));
        setSpaces(spcs.filter((s): s is Space => s !== null));
        setDevices(devs.filter((d): d is Device => d !== null));
        setPointDetails(pts.filter((p): p is PointDetail => p !== null));
      } catch {
        setError("リソース情報の取得に失敗しました。");
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, [router]);

  const handleClickBuilding = useCallback(
    (buildingId: string) => {
      router.push(`/buildings/${encodeURIComponent(buildingId)}`);
    },
    [router],
  );

  const handleClickFloor = useCallback(
    (floorDtId: string) => {
      router.push(`/floors/${encodeURIComponent(floorDtId)}`);
    },
    [router],
  );

  const handleClickSpace = useCallback(
    (spaceDtId: string) => {
      router.push(`/spaces/${encodeURIComponent(spaceDtId)}`);
    },
    [router],
  );

  const handleClickDevice = useCallback(
    (deviceDtId: string) => {
      router.push(`/devices/${encodeURIComponent(deviceDtId)}`);
    },
    [router],
  );

  const handleClickPoint = useCallback(
    (pointId: string) => {
      router.push(`/points/${encodeURIComponent(pointId)}`);
    },
    [router],
  );

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <div className="animate-spin rounded-full h-32 w-32 border-t-2 border-b-2 border-blue-500"></div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-4">
        <div className="bg-red-100 border border-red-400 text-red-700 px-4 py-3 rounded">
          {error}
        </div>
      </div>
    );
  }

  const hasNoResources =
    buildings.length === 0 &&
    floors.length === 0 &&
    spaces.length === 0 &&
    devices.length === 0 &&
    pointDetails.length === 0;

  return (
    <div className="container mx-auto px-4 py-8">
      <h1 className="text-2xl font-bold mb-6">マイリソース</h1>

      {hasNoResources && (
        <p className="text-gray-600">
          アクセス可能なリソースがありません。管理者にお問い合わせください。
        </p>
      )}

      {/* ビル */}
      {buildings.length > 0 && (
        <section className="mb-8">
          <h2 className="text-xl font-semibold mb-4">ビル</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
            {buildings.map((building) => (
              <div
                key={building.id}
                className="bg-white rounded-lg shadow-md p-6 hover:shadow-lg transition-shadow cursor-pointer"
                onClick={() => handleClickBuilding(building.id)}
              >
                <h3 className="text-lg font-semibold mb-2">
                  {building.name.length > 0 ? building.name : "名称未設定"}
                </h3>
                <p className="text-gray-600 truncate">ID: {building.id}</p>
              </div>
            ))}
          </div>
        </section>
      )}

      {/* フロア */}
      {floors.length > 0 && (
        <section className="mb-8">
          <h2 className="text-xl font-semibold mb-4">フロア</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
            {floors.map((floor) => (
              <div
                key={floor.id}
                className="bg-white rounded-lg shadow-md p-6 hover:shadow-lg transition-shadow cursor-pointer"
                onClick={() => handleClickFloor(floor.dtId)}
              >
                <h3 className="text-lg font-semibold mb-2">
                  {floor.name.length > 0 ? floor.name : "名称未設定"}
                </h3>
                <p className="text-gray-600 text-sm truncate">
                  ID: {floor.id}
                </p>
              </div>
            ))}
          </div>
        </section>
      )}

      {/* スペース */}
      {spaces.length > 0 && (
        <section className="mb-8">
          <h2 className="text-xl font-semibold mb-4">スペース</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
            {spaces.map((space) => (
              <div
                key={space.id}
                className="bg-white rounded-lg shadow-md p-6 hover:shadow-lg transition-shadow cursor-pointer"
                onClick={() => handleClickSpace(space.dtId)}
              >
                <h3 className="text-lg font-semibold mb-2">
                  {space.name.length > 0 ? space.name : "名称未設定"}
                </h3>
                <p className="text-gray-600 text-sm truncate">
                  ID: {space.id}
                </p>
              </div>
            ))}
          </div>
        </section>
      )}

      {/* デバイス */}
      {devices.length > 0 && (
        <section className="mb-8">
          <h2 className="text-xl font-semibold mb-4">デバイス</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
            {devices.map((device) => (
              <div
                key={device.id}
                className="bg-white rounded-lg shadow-md p-6 hover:shadow-lg transition-shadow cursor-pointer"
                onClick={() => handleClickDevice(device.dtId)}
              >
                <h3 className="text-lg font-semibold mb-2">
                  {device.name.length > 0 ? device.name : "名称未設定"}
                </h3>
                {device.deviceType && (
                  <p className="text-gray-600 text-sm">
                    種別: {toDisplayDeviceType(device.deviceType)}
                  </p>
                )}
                <p className="text-gray-400 text-xs mt-1 truncate">
                  ID: {device.id}
                </p>
              </div>
            ))}
          </div>
        </section>
      )}

      {/* ポイント */}
      {pointDetails.length > 0 && (
        <section className="mb-8">
          <h2 className="text-xl font-semibold mb-4">ポイント</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
            {pointDetails.map((pd) => (
              <div
                key={pd.point.id}
                className="bg-white rounded-lg shadow-md p-6 hover:shadow-lg transition-shadow cursor-pointer"
                onClick={() => handleClickPoint(pd.point.id)}
              >
                <h3 className="text-lg font-semibold mb-2">
                  {pd.point.name.length > 0 ? pd.point.name : "名称未設定"}
                </h3>
                {pd.device && (
                  <p className="text-gray-600 text-sm truncate">
                    デバイス: {pd.device.name}
                  </p>
                )}
                {pd.floor && (
                  <p className="text-gray-600 text-sm truncate">
                    フロア: {pd.floor.name}
                  </p>
                )}
                {pd.space && (
                  <p className="text-gray-600 text-sm truncate">
                    スペース: {pd.space.name}
                  </p>
                )}
                <p className="text-gray-400 text-xs mt-2 truncate">
                  ID: {pd.point.id}
                </p>
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
