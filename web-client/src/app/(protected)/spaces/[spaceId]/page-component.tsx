"use client";

import { apiClient } from "@/lib/infra/aspida-client";
import { Device, Space } from "@/lib/infra/aspida-client/generated/@types";
import { toDisplayDeviceType } from "@/lib/utils/helper/device-helper";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";

export default function SpaceDetailPageComponent({
  spaceId,
}: {
  spaceId: string;
}) {
  const router = useRouter();
  const [space, setSpace] = useState<Space | null>(null);
  const [devices, setDevices] = useState<Device[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchData = async () => {
      try {
        setLoading(true);
        const decodedSpaceId = decodeURIComponent(spaceId);
        const [spaceResult, devicesResult] = await Promise.all([
          apiClient().spaces._spaceDtId(encodeURIComponent(decodedSpaceId)).$get(),
          apiClient().devices.$get({ query: { spaceDtId: decodedSpaceId } }),
        ]);
        setSpace(spaceResult);
        setDevices(devicesResult);
      } catch {
        setError("スペース情報の取得に失敗しました。");
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, [spaceId]);

  const handleClickDevice = useCallback(
    (deviceDtId: string) => {
      router.push(`/devices/${encodeURIComponent(deviceDtId)}`);
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

  if (error || !space) {
    return (
      <div className="p-4">
        <div className="bg-red-100 border border-red-400 text-red-700 px-4 py-3 rounded">
          {error ?? "スペース情報が見つかりません。"}
        </div>
      </div>
    );
  }

  return (
    <div className="container mx-auto px-4 py-8">
      {/* 戻るボタン */}
      <div className="mb-4">
        <button
          onClick={() => router.back()}
          className="inline-flex items-center text-blue-600 hover:text-blue-800"
        >
          <span className="mr-1">←</span>
          戻る
        </button>
      </div>

      <h1 className="text-2xl font-bold mb-2">{space.name}</h1>
      <p className="text-gray-600 mb-6">スペース</p>

      <h2 className="text-xl font-semibold mb-4">デバイス一覧</h2>

      {devices.length === 0 ? (
        <p className="text-gray-600">デバイスがありません。</p>
      ) : (
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
              <p className="text-gray-400 text-xs mt-2 truncate">
                ID: {device.id}
              </p>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
