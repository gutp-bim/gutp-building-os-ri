"use client";

import { resourceTypeColor } from "@/lib/admin/permissions-display";
import { decodeDtIdForDisplay } from "@/lib/resources/format";
import { detailHref } from "@/lib/resources/keys";
import { sbcoClassName } from "@/lib/resources/sbco";
import type { ResourceMetadata, ResourceRef } from "@/lib/resources/types";
import Link from "next/link";

/**
 * Right-pane summary for the selected tree/search node. Shows identity metadata and a link to the
 * resource's standalone detail page (where the existing telemetry/control views live). Telemetry
 * charting stays on those pages — this pane is navigation + identity only (scope: A/B/E).
 */
export function ResourceDetail({
  resource,
  metadata,
  canWrite = false,
  onEditMetadata,
}: {
  resource: ResourceRef | null;
  metadata?: ResourceMetadata;
  canWrite?: boolean;
  onEditMetadata?: () => void;
}) {
  if (!resource) {
    return (
      <div
        className="flex h-full items-center justify-center text-sm text-gray-500"
        data-testid="detail-empty"
      >
        左のツリーから選択するか、検索してください。
      </div>
    );
  }

  return (
    <div className="p-4" data-testid="resource-detail">
      <div className="mb-4 flex items-center gap-2">
        <span
          className={`rounded px-2 py-0.5 text-xs font-medium ${resourceTypeColor(resource.type)}`}
          title={sbcoClassName(resource.type)}
        >
          {resource.type}
        </span>
        <h2 className="text-lg font-semibold">
          {resource.name || resource.id}
        </h2>
      </div>

      <dl className="grid grid-cols-[6rem_1fr] gap-y-2 text-sm">
        <dt className="font-semibold text-gray-600">クラス</dt>
        <dd className="truncate font-mono text-gray-700" title={sbcoClassName(resource.type)} data-testid="sbco-class">
          {sbcoClassName(resource.type)}
        </dd>
        <dt className="font-semibold text-gray-600">名称</dt>
        <dd className="truncate" title={resource.name}>
          {resource.name || "-"}
        </dd>
        <dt className="font-semibold text-gray-600">ID</dt>
        <dd className="truncate" title={resource.id}>
          {resource.id}
        </dd>
        <dt className="font-semibold text-gray-600">dtId</dt>
        <dd className="truncate text-gray-500" title={resource.dtId} data-testid="dtid">
          {decodeDtIdForDisplay(resource.dtId)}
        </dd>
      </dl>

      {metadata && (
        <section className="mt-6" data-testid="metadata-section">
          <div className="mb-2 flex items-center justify-between">
            <h3 className="text-sm font-semibold text-gray-700">メタデータ</h3>
            {canWrite && (
              <button
                type="button"
                className="rounded bg-gray-100 px-2 py-0.5 text-xs text-gray-600 hover:bg-gray-200"
                data-testid="metadata-edit-btn"
                onClick={onEditMetadata}
              >
                編集
              </button>
            )}
          </div>

          {Object.keys(metadata.identifiers).length > 0 && (
            <div className="mb-2">
              <p className="mb-1 text-xs font-medium text-gray-500">identifiers</p>
              <dl className="grid grid-cols-[8rem_1fr] gap-y-1 text-xs">
                {Object.entries(metadata.identifiers).map(([k, v]) => (
                  <>
                    <dt key={`ik-${k}`} className="truncate font-medium text-gray-700">{k}</dt>
                    <dd key={`iv-${k}`} className="truncate font-mono text-gray-600">{v}</dd>
                  </>
                ))}
              </dl>
            </div>
          )}

          {Object.keys(metadata.customTags).length > 0 && (
            <div>
              <p className="mb-1 text-xs font-medium text-gray-500">customTags</p>
              <dl className="grid grid-cols-[8rem_1fr] gap-y-1 text-xs">
                {Object.entries(metadata.customTags).map(([k, v]) => (
                  <>
                    <dt key={`tk-${k}`} className="truncate font-medium text-gray-700">{k}</dt>
                    <dd key={`tv-${k}`} className="text-gray-600">{v ? "true" : "false"}</dd>
                  </>
                ))}
              </dl>
            </div>
          )}
        </section>
      )}

      <Link
        href={detailHref(resource)}
        className="mt-6 inline-flex items-center rounded bg-blue-600 px-3 py-1.5 text-sm text-white hover:bg-blue-700"
      >
        詳細ページを開く →
      </Link>
    </div>
  );
}
