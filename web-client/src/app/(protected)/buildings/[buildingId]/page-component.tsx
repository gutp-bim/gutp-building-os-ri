"use client";

import { ResourceNavCard } from "@/components/resources/resource-nav-card";
import { InlineBanner } from "@/components/ui/inline-banner";
import { apiClient } from "@/lib/infra/aspida-client";
import { Floor } from "@/lib/infra/aspida-client/generated/@types";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

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

  return (
    <div className="container mx-auto px-4 py-8" data-testid="building-detail">
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

      <h1 className="mb-6 text-2xl font-bold">フロア一覧</h1>

      {loading ? (
        <p className="text-gray-600">読み込み中…</p>
      ) : error ? (
        <InlineBanner tone="error">{error}</InlineBanner>
      ) : floors.length === 0 ? (
        <p className="text-gray-600">フロアがありません。</p>
      ) : (
        <div className="grid grid-cols-1 gap-6 md:grid-cols-2 lg:grid-cols-3">
          {floors.map((floor) => (
            <ResourceNavCard
              key={floor.id}
              testId="floor-card"
              href={`/floors/${encodeURIComponent(floor.dtId)}`}
              name={floor.name.length > 0 ? floor.name : "名称未設定"}
              id={floor.id}
            />
          ))}
        </div>
      )}
    </div>
  );
}
