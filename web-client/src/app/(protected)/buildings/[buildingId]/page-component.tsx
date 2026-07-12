"use client";

import { apiClient } from "@/lib/infra/aspida-client";
import { Floor } from "@/lib/infra/aspida-client/generated/@types";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";

export default function BuildingDetailPageComponent({
  buildingId,
}: {
  buildingId: string;
}) {
  const router = useRouter();
  const [floors, setFloors] = useState<Floor[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchData = async () => {
      try {
        setLoading(true);
        const decodedBuildingId = decodeURIComponent(buildingId);
        const result = await apiClient().floors.$get({
          query: { buildingDtId: decodedBuildingId },
        });
        setFloors(result);
      } catch {
        setError("フロア情報の取得に失敗しました。");
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, [buildingId]);

  const handleClickFloor = useCallback(
    (floorDtId: string) => {
      router.push(`/floors/${encodeURIComponent(floorDtId)}`);
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

      <h1 className="text-2xl font-bold mb-6">フロア一覧</h1>

      {floors.length === 0 ? (
        <p className="text-gray-600">フロアがありません。</p>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
          {floors.map((floor) => (
            <div
              key={floor.id}
              className="bg-white rounded-lg shadow-md p-6 hover:shadow-lg transition-shadow cursor-pointer"
              onClick={() => handleClickFloor(floor.dtId)}
            >
              <h2 className="text-lg font-semibold mb-2">
                {floor.name.length > 0 ? floor.name : "名称未設定"}
              </h2>
              <p className="text-gray-600 text-sm truncate">ID: {floor.id}</p>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
