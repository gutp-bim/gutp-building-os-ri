"use client";

import {
  PointDetail,
  ValidTelemetryData,
} from "@/lib/infra/aspida-client/generated/@types";
import { getPointDetail } from "@/lib/resources/repository";
import { latestTelemetry, queryTelemetry } from "@/lib/telemetry/repository";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { ColdDataDownloadModal } from "./components/cold-data-download-modal";
import { PointControlModal } from "./components/point-control-modal/point-control-modal";
import { PointInfo } from "./components/point-info";
import { TelemetryHotData } from "./components/telemetry-hot-data";
import { TelemetryWarmData } from "./components/telemetry-warm-data";

export default function PointDetailPageComponent({
  pointId,
}: {
  pointId: string;
}) {
  const router = useRouter();
  const [pointDetail, setPointDetail] = useState<PointDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [hotData, setHotData] = useState<ValidTelemetryData | null>(null);
  const [warmData, setWarmData] = useState<ValidTelemetryData[]>([]);
  const [hotLoading, setHotLoading] = useState(false);
  const [warmLoading, setWarmLoading] = useState(false);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [startDate, setStartDate] = useState("");
  const [endDate, setEndDate] = useState("");
  const [coldLoading, setColdLoading] = useState(false);

  useEffect(() => {
    const fetchPointDetail = async () => {
      try {
        setLoading(true);
        const result = await getPointDetail(pointId);
        setPointDetail(result);
      } catch {
        setError("ポイント情報の取得に失敗しました。");
      } finally {
        setLoading(false);
      }
    };
    fetchPointDetail();
  }, [pointId]);

  const fetchHotData = async () => {
    if (!pointDetail?.point.id) return;
    try {
      setHotLoading(true);
      const latest = await latestTelemetry(pointDetail.point.id);
      setHotData(latest ? { value: latest.v, datetime: latest.t } : null);
    } catch (e) {
      console.error(e);
    } finally {
      setHotLoading(false);
    }
  };

  const fetchWarmData = async () => {
    if (!pointDetail?.point.id) return;
    try {
      setWarmLoading(true);
      const end = new Date();
      const start = new Date(end.getTime() - 24 * 60 * 60 * 1000);
      const series = await queryTelemetry({
        pointId: pointDetail.point.id,
        start,
        end,
      });
      setWarmData(series.points.map((p) => ({ datetime: p.t, value: p.v })));
    } catch (e) {
      console.error(e);
    } finally {
      setWarmLoading(false);
    }
  };

  const handleDownloadCold = async () => {
    if (!pointDetail?.point.id) return;
    try {
      setColdLoading(true);
      const series = await queryTelemetry({
        pointId: pointDetail.point.id,
        start: new Date(startDate),
        end: new Date(endDate),
      });
      const csvData = [
        "日時,値",
        ...series.points.map(
          (p) => `${new Date(p.t).toLocaleString("ja-JP")},${p.v}`,
        ),
      ].join("\n");
      const blob = new Blob([csvData], { type: "text/csv" });
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `telemetry_${pointDetail.point.id}_${
        new Date().toISOString().split("T")[0]
      }.csv`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      window.URL.revokeObjectURL(url);
      setIsModalOpen(false);
    } catch (e) {
      console.error(e);
    } finally {
      setColdLoading(false);
    }
  };

  useEffect(() => {
    if (pointDetail) {
      fetchHotData();
      fetchWarmData();
    }
  }, [pointDetail]);

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <div className="animate-spin rounded-full h-32 w-32 border-t-2 border-b-2 border-blue-500"></div>
      </div>
    );
  }

  if (error || !pointDetail) {
    return (
      <div className="p-4">
        <div className="bg-red-100 border border-red-400 text-red-700 px-4 py-3 rounded">
          {error ?? "ポイント情報が見つかりません。"}
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

      <div className="flex gap-4 mb-8">
        <div className={"flex flex-col gap-4 w-1/2 flex-shrink-0"}>
          <PointInfo pointDetail={pointDetail} />
          <PointControlModal pointDetail={pointDetail} />
        </div>
        <TelemetryHotData
          hotData={hotData}
          hotLoading={hotLoading}
          onRefresh={fetchHotData}
          onDownloadClick={() => setIsModalOpen(true)}
          scale={pointDetail.point.scale ?? undefined}
          unit={pointDetail.point.unit ?? undefined}
          labels={pointDetail.point.labels ?? undefined}
        />
      </div>

      <TelemetryWarmData
        warmData={warmData}
        warmLoading={warmLoading}
        onRefresh={fetchWarmData}
      />

      <ColdDataDownloadModal
        isOpen={isModalOpen}
        onClose={() => setIsModalOpen(false)}
        startDate={startDate}
        endDate={endDate}
        onStartDateChange={setStartDate}
        onEndDateChange={setEndDate}
        onDownload={handleDownloadCold}
        isLoading={coldLoading}
      />
    </div>
  );
}
