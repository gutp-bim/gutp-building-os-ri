"use client";

import { resourceTypeColor } from "@/lib/admin/permissions-display";
import { decodeDtIdForDisplay } from "@/lib/resources/format";
import { detailHref } from "@/lib/resources/keys";
import { sbcoClassName } from "@/lib/resources/sbco";
import type { ResourceRef } from "@/lib/resources/types";
import Link from "next/link";

/**
 * Right-pane summary for the selected tree/search node. Shows identity metadata and a link to the
 * resource's standalone detail page (where the existing telemetry/control views live). Telemetry
 * charting stays on those pages — this pane is navigation + identity only (scope: A/B/E).
 */
export function ResourceDetail({ resource }: { resource: ResourceRef | null }) {
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

      <Link
        href={detailHref(resource)}
        className="mt-6 inline-flex items-center rounded bg-blue-600 px-3 py-1.5 text-sm text-white hover:bg-blue-700"
      >
        詳細ページを開く →
      </Link>
    </div>
  );
}
