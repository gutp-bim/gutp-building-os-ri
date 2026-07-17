"use client";

import { ResourceNavCard } from "@/components/resources/resource-nav-card";
import { InlineBanner } from "@/components/ui/inline-banner";
import { apiClient } from "@/lib/infra/aspida-client";
import { Device, Space } from "@/lib/infra/aspida-client/generated/@types";
import { toDisplayDeviceType } from "@/lib/utils/helper/device-helper";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

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

  return (
    <div className="container mx-auto px-4 py-8" data-testid="space-detail">
      <div className="mb-4">
        <button
          type="button"
          onClick={() => router.back()}
          className="inline-flex items-center text-blue-600 hover:text-blue-800"
        >
          <span className="mr-1">←</span>
          戻る
        </button>
      </div>

      {loading ? (
        <p className="text-gray-600">読み込み中…</p>
      ) : error || !space ? (
        <InlineBanner tone="error">
          {error ?? "スペース情報が見つかりません。"}
        </InlineBanner>
      ) : (
        <>
          <h1 className="mb-2 text-2xl font-bold">{space.name}</h1>
          <p className="mb-6 text-gray-600">スペース</p>

          <h2 className="mb-4 text-xl font-semibold">デバイス一覧</h2>

          {devices.length === 0 ? (
            <p className="text-gray-600">デバイスがありません。</p>
          ) : (
            <div className="grid grid-cols-1 gap-6 md:grid-cols-2 lg:grid-cols-3">
              {devices.map((device) => (
                <ResourceNavCard
                  key={device.id}
                  testId="device-card"
                  href={`/devices/${encodeURIComponent(device.dtId)}`}
                  name={device.name.length > 0 ? device.name : "名称未設定"}
                  subtitle={
                    device.deviceType
                      ? `種別: ${toDisplayDeviceType(device.deviceType)}`
                      : undefined
                  }
                  id={device.id}
                />
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}
