"use client";

import { ResourceNavCard } from "@/components/resources/resource-nav-card";
import { InlineBanner } from "@/components/ui/inline-banner";
import { apiClient } from "@/lib/infra/aspida-client";
import { Floor, Space } from "@/lib/infra/aspida-client/generated/@types";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

export default function FloorDetailPageComponent({
  floorId,
}: {
  floorId: string;
}) {
  const router = useRouter();
  const [floor, setFloor] = useState<Floor | null>(null);
  const [spaces, setSpaces] = useState<Space[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchData = async () => {
      try {
        setLoading(true);
        const decodedFloorId = decodeURIComponent(floorId);
        const [floorResult, spacesResult] = await Promise.all([
          apiClient().floors._floorDtId(encodeURIComponent(decodedFloorId)).$get(),
          apiClient().spaces.$get({ query: { floorDtId: decodedFloorId } }),
        ]);
        setFloor(floorResult);
        setSpaces(spacesResult);
      } catch {
        setError("フロア情報の取得に失敗しました。");
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, [floorId]);

  return (
    <div className="container mx-auto px-4 py-8" data-testid="floor-detail">
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
      ) : error || !floor ? (
        <InlineBanner tone="error">
          {error ?? "フロア情報が見つかりません。"}
        </InlineBanner>
      ) : (
        <>
          <h1 className="mb-2 text-2xl font-bold">{floor.name}</h1>
          <p className="mb-6 text-gray-600">フロア</p>

          <h2 className="mb-4 text-xl font-semibold">スペース一覧</h2>

          {spaces.length === 0 ? (
            <p className="text-gray-600">スペースがありません。</p>
          ) : (
            <div className="grid grid-cols-1 gap-6 md:grid-cols-2 lg:grid-cols-3">
              {spaces.map((space) => (
                <ResourceNavCard
                  key={space.id}
                  testId="space-card"
                  href={`/spaces/${encodeURIComponent(space.dtId)}`}
                  name={space.name.length > 0 ? space.name : "名称未設定"}
                  id={space.id}
                />
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}
