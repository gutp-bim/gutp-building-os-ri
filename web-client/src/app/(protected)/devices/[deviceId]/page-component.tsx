"use client";

import { FilterPopup } from "@/components/table/FilterPopup";
import { Pagination } from "@/components/table/Pagination";
import { TableHeader } from "@/components/table/TableHeader";
import { InlineBanner } from "@/components/ui/inline-banner";
import { useTable } from "@/contexts/TableContext";
import { apiClient } from "@/lib/infra/aspida-client";
import { Device, Point } from "@/lib/infra/aspida-client/generated/@types";
import { toDisplayDeviceType } from "@/lib/utils/helper/device-helper";
import { PointTableField, PointTableHeader } from "@/types/point-table";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";

export default function DeviceDetailPageComponent({
  deviceId,
}: {
  deviceId: string;
}) {
  const router = useRouter();
  const [device, setDevice] = useState<Device | null>(null);
  const [points, setPoints] = useState<Point[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchData = async () => {
      try {
        setLoading(true);
        const enc = encodeURIComponent;
        // params.deviceId from Next.js 15 App Router may still be percent-encoded
        // (e.g. "urn%3Anext%3A...") when the URL segment itself was encoded.
        // Decode before API calls so Aspida/Axios does not double-encode the value.
        const decodedDeviceId = decodeURIComponent(deviceId);
        const [deviceResult, pointsResult] = await Promise.all([
          apiClient().devices._deviceDtId(enc(decodedDeviceId)).$get(),
          apiClient().points.$get({ query: { deviceDtId: decodedDeviceId } }),
        ]);
        setDevice(deviceResult);
        setPoints(pointsResult);
      } catch {
        setError("デバイス情報の取得に失敗しました。");
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, [deviceId]);

  return (
    <div className="container mx-auto px-4 py-8" data-testid="device-detail">
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
      ) : error || !device ? (
        <InlineBanner tone="error">
          {error ?? "デバイス情報が見つかりません。"}
        </InlineBanner>
      ) : (
        <>
          <div className="grow-2 rounded-lg bg-white p-4 shadow">
            <h2 className="mb-4 text-xl font-bold">{device.name}</h2>
            <div className="space-y-2 text-gray-700">
              <div>Owner : {device.owner}</div>
              <div>Site : {device.site}</div>
              <div>Supplier : {device.supplier}</div>
              <div>Gateway ID : {device.gatewayId}</div>
              <div>Device Type : {toDisplayDeviceType(device.deviceType ?? "")}</div>
            </div>
          </div>

          <PointTableContent points={points} />
        </>
      )}
    </div>
  );
}

function PointTableContent({ points }: { points: Point[] }) {
  const { state, dispatch } = useTable<PointTableField>();
  const filterButtonRefs = useRef<{
    [key in PointTableField]: HTMLButtonElement | null;
  }>({
    name: null,
    dataSpecification: null,
    dataType: null,
    writable: null,
    targetArea: null,
  });

  const headers: PointTableHeader[] = [
    { field: "name", label: "ポイント名" },
    { field: "dataSpecification", label: "データ仕様" },
    { field: "dataType", label: "データ型" },
    { field: "writable", label: "書き込み可否" },
    { field: "targetArea", label: "対象エリア" },
  ];

  const handleSort = (field: PointTableField) => {
    if (state.sortField === field) {
      if (state.sortDirection === "asc") {
        dispatch({ type: "SET_SORT", payload: { field, direction: "desc" } });
      } else if (state.sortDirection === "desc") {
        dispatch({ type: "CLEAR_SORT" });
      }
    } else {
      dispatch({ type: "SET_SORT", payload: { field, direction: "asc" } });
    }
  };

  const handleFilterClick = (field: PointTableField) => {
    const button = filterButtonRefs.current[field];
    if (button) {
      const rect = button.getBoundingClientRect();
      dispatch({
        type: "SET_ACTIVE_FILTER",
        payload: {
          field,
          position: {
            top: rect.bottom + window.scrollY + 5,
            left: rect.left + window.scrollX,
          },
        },
      });
    }
  };

  const handleFilterClose = () => {
    dispatch({ type: "SET_ACTIVE_FILTER", payload: { field: null } });
  };

  const handleFilterChange = (
    field: PointTableField,
    selectedItems: string[],
  ) => {
    dispatch({
      type: "UPDATE_FILTERS",
      payload: { field, items: selectedItems },
    });
  };

  const getFilterItems = (field: PointTableField): string[] => {
    const items = new Set<string>();
    points.forEach((point) => {
      const value = getSortValue(point, field);
      if (value) items.add(value);
    });
    return Array.from(items).sort();
  };

  const getSortValue = (point: Point, field: PointTableField): string => {
    switch (field) {
      case "name":
        return point.name;
      case "dataSpecification":
        return point.specification ?? "";
      case "dataType":
        return point.type ?? "";
      case "writable":
        return point.writable ? "可" : "不可";
      case "targetArea":
        return point.targetArea ?? "";
      default:
        return "";
    }
  };

  const sortedAndFilteredPoints = [...points]
    .filter((point) => {
      return Object.entries(state.filters).every(([field, selectedItems]) => {
        if (selectedItems.length === 0) return true;
        const value = getSortValue(point, field as PointTableField);
        return selectedItems.includes(value);
      });
    })
    .sort((a, b) => {
      if (!state.sortField || !state.sortDirection) return 0;

      const aValue = getSortValue(a, state.sortField);
      const bValue = getSortValue(b, state.sortField);

      if (state.sortDirection === "asc") {
        return aValue.localeCompare(bValue);
      } else {
        return bValue.localeCompare(aValue);
      }
    });

  const totalPages = Math.ceil(sortedAndFilteredPoints.length / state.pageSize);
  const paginatedPoints = sortedAndFilteredPoints.slice(
    (state.currentPage - 1) * state.pageSize,
    state.currentPage * state.pageSize,
  );

  return (
    <div className="mt-8">
      <h2 className="text-xl font-bold mb-4">ポイント一覧</h2>

      <div className="overflow-x-auto">
        <table className="min-w-full bg-white shadow-sm rounded-lg">
          <thead className="bg-gray-50">
            <tr>
              {headers.map(({ field, label }) => (
                <TableHeader<PointTableField>
                  key={field}
                  field={field}
                  label={label}
                  isFiltered={state.filters[field].length > 0}
                  isHovered={state.hoveredField === field}
                  isSorted={state.sortField === field}
                  sortField={state.sortField}
                  sortDirection={state.sortDirection}
                  onSort={handleSort}
                  onFilterClick={handleFilterClick}
                  onMouseEnter={(field) =>
                    dispatch({ type: "SET_HOVERED_FIELD", payload: field })
                  }
                  onMouseLeave={() =>
                    dispatch({ type: "SET_HOVERED_FIELD", payload: null })
                  }
                  filterButtonRef={(el) => {
                    if (el) filterButtonRefs.current[field] = el;
                  }}
                />
              ))}
            </tr>
          </thead>
          <tbody className="bg-white divide-y divide-gray-200">
            {paginatedPoints.map((point) => (
              <tr key={point.id} data-testid="device-point-row" className="hover:bg-gray-50">
                {/* Keyboard-accessible: the point name is a real link (Tab-focusable, Enter/⌘-click),
                    replacing the old mouse-only `<tr onClick>` (#195). */}
                <td className="px-6 py-4 whitespace-nowrap">
                  <Link
                    href={`/points/${encodeURIComponent(point.id)}`}
                    className="font-medium text-blue-600 hover:text-blue-800 hover:underline"
                  >
                    {point.name}
                  </Link>
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  {point.specification}
                </td>
                <td className="px-6 py-4 whitespace-nowrap">{point.type}</td>
                <td className="px-6 py-4 whitespace-nowrap">
                  {point.writable ? "可" : "不可"}
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  {point.targetArea}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <Pagination
        currentPage={state.currentPage}
        totalPages={Math.max(totalPages, 1)}
        pageSize={state.pageSize}
        onPageChange={(page) => dispatch({ type: "SET_PAGE", payload: page })}
        onPageSizeChange={(size) =>
          dispatch({ type: "SET_PAGE_SIZE", payload: size })
        }
      />

      {state.activeFilter && (
        <FilterPopup<PointTableField>
          field={state.activeFilter}
          items={getFilterItems(state.activeFilter)}
          selectedItems={state.filters[state.activeFilter]}
          onClose={handleFilterClose}
          onFilterChange={handleFilterChange}
          position={state.filterPosition}
        />
      )}
    </div>
  );
}
