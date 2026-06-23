"use client";

import { useState } from "react";
import { resourceTypeColor } from "@/lib/admin/permissions-display";
import {
  EDIT_RESOURCE_TYPES,
  validateResourceId,
  type EditResourceType,
} from "@/lib/admin/resource-edit";
import type { AdminGroupResourceItem } from "@/lib/admin/types";
import { ResourceTreePicker } from "./resource-tree-picker";

/**
 * Editable group resource list (#143): existing items as removable rows + an add form. Presentational
 * — it calls `onAdd(resourceType, resourceId)` / `onRemove(itemId)` and the parent runs the mutation.
 * The id can be typed raw or picked via the shared {@link ResourceTreePicker}.
 */
export function GroupResourceManager({
  items,
  busy,
  onAdd,
  onRemove,
}: {
  items: AdminGroupResourceItem[];
  busy?: boolean;
  onAdd: (resourceType: EditResourceType, resourceId: string) => void;
  onRemove: (itemId: string) => void;
}) {
  const [resourceType, setResourceType] = useState<EditResourceType>("device");
  const [resourceId, setResourceId] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);

  const handleAdd = () => {
    const result = validateResourceId(resourceId);
    if (!result.ok) {
      setError(result.error);
      return;
    }
    setError(null);
    onAdd(resourceType, resourceId.trim());
    setResourceId("");
  };

  return (
    <div data-testid="group-resource-manager">
      {items.length === 0 ? (
        <p className="text-sm text-gray-500" data-testid="resources-empty">
          リソースはありません
        </p>
      ) : (
        <ul className="flex flex-col gap-1" data-testid="resource-list">
          {items.map((item) => (
            <li
              key={item.id ?? `${item.resourceType}:${item.resourceId}`}
              className="flex items-center gap-2 text-sm"
              data-testid={`resource-${item.id}`}
            >
              <span
                className={`rounded px-2 py-0.5 text-xs font-medium ${resourceTypeColor(item.resourceType ?? "")}`}
              >
                {item.resourceType || "—"}
              </span>
              <span className="font-mono text-xs text-gray-600">{item.resourceId || "—"}</span>
              {item.id && (
                <button
                  type="button"
                  onClick={() => onRemove(item.id as string)}
                  disabled={busy}
                  className="ml-auto rounded border border-red-300 px-2 py-0.5 text-xs text-red-600 hover:bg-red-50 disabled:opacity-50"
                  aria-label={`リソース ${item.resourceType}:${item.resourceId} を削除`}
                >
                  削除
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      <div className="mt-4 rounded border border-gray-200 p-3" data-testid="resource-add-form">
        <h3 className="mb-2 text-sm font-semibold">リソースを追加</h3>
        {error && (
          <p className="mb-2 text-sm text-red-600" data-testid="resource-add-error">
            {error}
          </p>
        )}
        <div className="flex flex-wrap items-center gap-2">
          <select
            aria-label="リソース種別"
            value={resourceType}
            onChange={(e) => setResourceType(e.target.value as EditResourceType)}
            className="rounded border border-gray-300 px-2 py-1 text-sm"
          >
            {EDIT_RESOURCE_TYPES.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>
          <input
            aria-label="リソース ID"
            type="text"
            value={resourceId}
            onChange={(e) => setResourceId(e.target.value)}
            placeholder="リソース ID（dtId 等）"
            className="flex-1 rounded border border-gray-300 px-2 py-1 text-sm"
          />
          <button
            type="button"
            onClick={() => setPickerOpen(true)}
            className="rounded border border-gray-300 px-2 py-1 text-sm hover:bg-gray-50"
          >
            ツリーから選択
          </button>
          <button
            type="button"
            onClick={handleAdd}
            disabled={busy}
            className="rounded bg-blue-600 px-3 py-1 text-sm text-white hover:bg-blue-700 disabled:opacity-50"
          >
            追加
          </button>
        </div>
      </div>

      {pickerOpen && (
        <ResourceTreePicker
          onSelect={(type, id) => {
            setResourceType(type);
            setResourceId(id);
            setPickerOpen(false);
          }}
          onClose={() => setPickerOpen(false)}
        />
      )}
    </div>
  );
}
