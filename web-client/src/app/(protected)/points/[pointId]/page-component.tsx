"use client";

import { InlineBanner } from "@/components/ui/inline-banner";
import {
  PointDetail,
  ValidTelemetryData,
} from "@/lib/infra/aspida-client/generated/@types";
import { getPointDetail } from "@/lib/resources/repository";
import { latestTelemetry, queryTelemetry } from "@/lib/telemetry/repository";
import {
  DEFAULT_GRANULARITY,
  DEFAULT_PERIOD,
  effectiveRange,
  resolveGranularity,
  type GranularityOption,
  type PeriodPreset,
} from "@/lib/telemetry/range";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { ColdDataDownloadModal } from "./components/cold-data-download-modal";
import { ControlAuditHistory } from "./components/control-audit-history";
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
  // Telemetry read failures used to fall to console.error only; surface them inline (#196) so the
  // operator can tell "unavailable" apart from "no data".
  const [hotError, setHotError] = useState<string | null>(null);
  const [warmError, setWarmError] = useState<string | null>(null);
  const [coldError, setColdError] = useState<string | null>(null);
  const [period, setPeriod] = useState<PeriodPreset>(DEFAULT_PERIOD);
  const [granularity, setGranularity] = useState<GranularityOption>(DEFAULT_GRANULARITY);
  // Monotonic id: a warm response for a superseded period/granularity must not overwrite the current
  // selection, and only the latest request may clear the loading/error state (#197 review).
  const warmRequestId = useRef(0);

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
      setHotError(null);
      const latest = await latestTelemetry(pointDetail.point.id);
      setHotData(latest ? { value: latest.v, datetime: latest.t } : null);
    } catch (e) {
      console.error(e);
      setHotError("最新値の取得に失敗しました。");
    } finally {
      setHotLoading(false);
    }
  };

  const fetchWarmData = async () => {
    if (!pointDetail?.point.id) return;
    const requestId = ++warmRequestId.current;
    try {
      setWarmLoading(true);
      setWarmError(null);
      const { start, end } = effectiveRange(period, new Date());
      const series = await queryTelemetry({
        pointId: pointDetail.point.id,
        start,
        end,
        granularity: resolveGranularity(granularity, period),
      });
      // A newer period/granularity request has superseded this one — drop its (stale) result so it
      // can't overwrite the current chart or prematurely clear loading.
      if (requestId !== warmRequestId.current) return;
      setWarmData(series.points.map((p) => ({ datetime: p.t, value: p.v })));
    } catch (e) {
      if (requestId !== warmRequestId.current) return;
      console.error(e);
      setWarmError("履歴データの取得に失敗しました。");
    } finally {
      if (requestId === warmRequestId.current) setWarmLoading(false);
    }
  };

  const handleDownloadCold = async () => {
    if (!pointDetail?.point.id) return;
    try {
      setColdLoading(true);
      setColdError(null);
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
      // Keep the modal open so the operator sees the failure and can retry.
      setColdError("CSV のダウンロードに失敗しました。");
    } finally {
      setColdLoading(false);
    }
  };

  useEffect(() => {
    if (pointDetail) fetchHotData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pointDetail]);

  // Refetch the history whenever the point, period, or granularity changes (#197).
  useEffect(() => {
    if (pointDetail) fetchWarmData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pointDetail, period, granularity]);

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
        <div className="flex flex-1 flex-col gap-2">
          {hotError && (
            <InlineBanner tone="error" testId="hot-error" onDismiss={() => setHotError(null)}>
              {hotError}
            </InlineBanner>
          )}
          <TelemetryHotData
            hotData={hotData}
            hotLoading={hotLoading}
            onRefresh={fetchHotData}
            onDownloadClick={() => {
              setColdError(null);
              setIsModalOpen(true);
            }}
            scale={pointDetail.point.scale ?? undefined}
            unit={pointDetail.point.unit ?? undefined}
            labels={pointDetail.point.labels ?? undefined}
          />
        </div>
      </div>

      {warmError && (
        <div className="mb-2">
          <InlineBanner tone="error" testId="warm-error" onDismiss={() => setWarmError(null)}>
            {warmError}
          </InlineBanner>
        </div>
      )}
      <TelemetryWarmData
        warmData={warmData}
        warmLoading={warmLoading}
        onRefresh={fetchWarmData}
        period={period}
        granularity={granularity}
        onPeriodChange={setPeriod}
        onGranularityChange={setGranularity}
        unit={pointDetail.point.unit ?? undefined}
      />

      <ControlAuditHistory pointId={pointDetail.point.id} />

      <ColdDataDownloadModal
        isOpen={isModalOpen}
        onClose={() => {
          setColdError(null);
          setIsModalOpen(false);
        }}
        startDate={startDate}
        endDate={endDate}
        onStartDateChange={setStartDate}
        onEndDateChange={setEndDate}
        onDownload={handleDownloadCold}
        isLoading={coldLoading}
        error={coldError}
      />
    </div>
  );
}
