"use client";

import { InlineBanner } from "@/components/ui/inline-banner";
import { getPointDetail } from "@/lib/resources/repository";
import type { PointDetailResource } from "@/lib/resources/types";
import { toTelemetryCsv } from "@/lib/telemetry/csv";
import {
  autoGranularityForSpan,
  dateRangeError,
  DEFAULT_GRANULARITY,
  DEFAULT_PERIOD,
  effectiveRange,
  isValidDateRange,
  rangeSpansMultipleDays,
  resolveGranularity,
  type GranularityOption,
  type PeriodValue,
} from "@/lib/telemetry/range";
import {
  getTelemetryConfig,
  latestTelemetrySample,
  queryTelemetryWithState,
  type TelemetryConfig,
} from "@/lib/telemetry/repository";
import type {
  Granularity,
  TelemetryLatestSample,
  TelemetryPoint,
  TelemetryStatePoint,
} from "@/lib/telemetry/types";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { ColdDataDownloadModal } from "./components/cold-data-download-modal";
import { CONTROL_AUDIT_ANCHOR_ID } from "./components/control-audit-anchor";
import { ControlAuditHistory } from "./components/control-audit-history";
import { PointControlModal } from "./components/point-control-modal/point-control-modal";
import { PointInfo } from "./components/point-info";
import { TelemetryHotData } from "./components/telemetry-hot-data";
import { TelemetryStateTimeline } from "./components/telemetry-state-timeline";
import { TelemetryWarmData } from "./components/telemetry-warm-data";

export default function PointDetailPageComponent({
  pointId,
}: {
  pointId: string;
}) {
  const router = useRouter();
  const [pointDetail, setPointDetail] = useState<PointDetailResource | null>(
    null,
  );
  // Bumped when a control settles, so 制御履歴 refetches and shows the command just issued (#162).
  const [controlAuditReloadKey, setControlAuditReloadKey] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [hotData, setHotData] = useState<TelemetryLatestSample | null>(null);
  const [warmData, setWarmData] = useState<TelemetryPoint[]>([]);
  // #152 Phase B: non-numeric (string/boolean) history for the state timeline.
  const [stateData, setStateData] = useState<TelemetryStatePoint[]>([]);
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
  const [period, setPeriod] = useState<PeriodValue>(DEFAULT_PERIOD);
  const [granularity, setGranularity] =
    useState<GranularityOption>(DEFAULT_GRANULARITY);
  // Custom-range inputs (#197), only consulted when period === "custom".
  const [customStart, setCustomStart] = useState("");
  const [customEnd, setCustomEnd] = useState("");
  const [telemetryConfig, setTelemetryConfig] =
    useState<TelemetryConfig | null>(null);

  // Effective stale thresholds (system default + admin override) for the freshness badge (#183). The
  // fetch is cached in the façade; failure falls back to the defaults inside getTelemetryConfig.
  useEffect(() => {
    let active = true;
    getTelemetryConfig().then((cfg) => {
      if (active) setTelemetryConfig(cfg);
    });
    return () => {
      active = false;
    };
  }, []);
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
      // Raw latest sample (not the numeric-only series) so a string/boolean reading survives (#152).
      const latest = await latestTelemetrySample(pointDetail.point.id);
      setHotData(latest);
    } catch (e) {
      console.error(e);
      setHotError("最新値の取得に失敗しました。");
    } finally {
      setHotLoading(false);
    }
  };

  const fetchWarmData = async () => {
    if (!pointDetail?.point.id) return;
    const now = new Date();
    // Resolve the query window + granularity from the period (preset span, or a validated custom
    // range). A custom range that is incomplete or invalid is skipped — the inline guard explains
    // why — so we never fire a nonsensical start ≥ end / future query (#197).
    let start: Date;
    let end: Date;
    let queryGranularity: Granularity;
    if (period === "custom") {
      if (!isValidDateRange(customStart, customEnd, now)) return;
      start = new Date(customStart);
      end = new Date(customEnd);
      queryGranularity =
        granularity === "auto"
          ? autoGranularityForSpan(start, end)
          : granularity;
    } else {
      ({ start, end } = effectiveRange(period, now));
      queryGranularity = resolveGranularity(granularity, period);
    }
    const requestId = ++warmRequestId.current;
    try {
      setWarmLoading(true);
      setWarmError(null);
      // One fetch → numeric chart series + non-numeric state series (#152 Phase B).
      const { series, state } = await queryTelemetryWithState({
        pointId: pointDetail.point.id,
        start,
        end,
        granularity: queryGranularity,
      });
      // A newer period/granularity request has superseded this one — drop its (stale) result so it
      // can't overwrite the current chart or prematurely clear loading.
      if (requestId !== warmRequestId.current) return;
      setWarmData(series.points);
      setStateData(state.points);
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
      // Raw, deliberately — do NOT inherit the chart's granularity.
      //
      // This modal takes its own start/end and is the cold-data export (hence
      // ColdDataDownloadModal): it hands over the readings for a chosen range, not a copy of the
      // chart. Asking for an aggregated granularity breaks that in three ways:
      //   - OssTelemetryQueryRouter.QueryAggregatedAsync only consults the aggregate and warm
      //     stores, never the cold one — so with WARM_STORE=timescale a range older than warm
      //     retention comes back empty, from the button whose whole job is old data.
      //   - the Timescale aggregate stores select only `value`, so a non-numeric point would once
      //     again export as a bare header.
      //   - the modal shows no granularity control, so it would silently inherit whatever the chart
      //     was left on: an hour-long export with the chart on 「1日」 collapses to a single row.
      // Aggregated numbers are also the wrong artefact here — a bucket average looks exactly like a
      // reading, and min/max/count are dropped.
      //
      // Both halves of the trend are still exported: the numeric series AND the state series.
      const { series, state } = await queryTelemetryWithState({
        pointId: pointDetail.point.id,
        start: new Date(startDate),
        end: new Date(endDate),
      });
      const csvData = toTelemetryCsv({
        series: series.points,
        state: state.points,
      });
      const blob = new Blob([csvData], { type: "text/csv;charset=utf-8" });
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

  // Refetch the history whenever the point, period, granularity, or custom range changes (#197).
  useEffect(() => {
    if (pointDetail) fetchWarmData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pointDetail, period, granularity, customStart, customEnd]);

  if (loading) {
    return (
      <div className="container mx-auto px-4 py-8">
        <p className="text-gray-600">読み込み中…</p>
      </div>
    );
  }

  if (error || !pointDetail) {
    return (
      <div className="container mx-auto px-4 py-8">
        <InlineBanner tone="error">
          {error ?? "ポイント情報が見つかりません。"}
        </InlineBanner>
      </div>
    );
  }

  return (
    <div className="container mx-auto px-4 py-8" data-testid="point-detail">
      {/* 戻るボタン */}
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

      <div className="flex gap-4 mb-8">
        <div className={"flex flex-col gap-4 w-1/2 flex-shrink-0"}>
          <PointInfo pointDetail={pointDetail} />
          <PointControlModal
            pointDetail={pointDetail}
            onControlSettled={() => setControlAuditReloadKey((n) => n + 1)}
          />
        </div>
        <div className="flex flex-1 flex-col gap-2">
          {hotError && (
            <InlineBanner
              tone="error"
              testId="hot-error"
              onDismiss={() => setHotError(null)}
            >
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
            expectedIntervalSeconds={
              pointDetail.point.expectedIntervalSeconds ?? undefined
            }
            staleThresholdSeconds={telemetryConfig?.staleThresholdSeconds}
            staleIntervalMultiplier={telemetryConfig?.staleIntervalMultiplier}
          />
        </div>
      </div>

      {warmError && (
        <div className="mb-2">
          <InlineBanner
            tone="error"
            testId="warm-error"
            onDismiss={() => setWarmError(null)}
          >
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
        customStart={customStart}
        customEnd={customEnd}
        onCustomStartChange={setCustomStart}
        onCustomEndChange={setCustomEnd}
        rangeError={
          period === "custom"
            ? dateRangeError(customStart, customEnd, new Date())
            : null
        }
        multiDay={
          period === "custom" &&
          isValidDateRange(customStart, customEnd, new Date())
            ? rangeSpansMultipleDays(new Date(customStart), new Date(customEnd))
            : undefined
        }
      />

      {stateData.length > 0 && (
        <TelemetryStateTimeline points={stateData} loading={warmLoading} />
      )}

      <div id={CONTROL_AUDIT_ANCHOR_ID}>
        <ControlAuditHistory
          pointId={pointDetail.point.id}
          reloadKey={controlAuditReloadKey}
        />
      </div>

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
