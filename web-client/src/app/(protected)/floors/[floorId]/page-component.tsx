"use client";

import { apiClient } from "@/lib/infra/aspida-client";
import { Floor, Space } from "@/lib/infra/aspida-client/generated/@types";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";

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

  const handleClickSpace = useCallback(
    (spaceDtId: string) => {
      router.push(`/spaces/${encodeURIComponent(spaceDtId)}`);
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

  if (error || !floor) {
    return (
      <div className="p-4">
        <div className="bg-red-100 border border-red-400 text-red-700 px-4 py-3 rounded">
          {error ?? "フロア情報が見つかりません。"}
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

      <h1 className="text-2xl font-bold mb-2">{floor.name}</h1>
      <p className="text-gray-500 mb-6">フロア</p>

      <h2 className="text-xl font-semibold mb-4">スペース一覧</h2>

      {spaces.length === 0 ? (
        <p className="text-gray-500">スペースがありません。</p>
      ) : (
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
              <p className="text-gray-600 text-sm truncate">ID: {space.id}</p>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
