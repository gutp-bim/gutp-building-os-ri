"use client";

import { useState } from "react";
import {
  actionLabel,
  parsePermission,
  resourceTypeColor,
} from "@/lib/admin/permissions-display";
import {
  EDIT_ACTIONS,
  EDIT_RESOURCE_TYPES,
  buildPermissionString,
  validatePermissionInput,
  type EditAction,
  type EditResourceType,
} from "@/lib/admin/permission-edit";
import { resolveDisplay, type ResolvedMap } from "@/lib/admin/permission-resolve";
import { ResourceTreePicker } from "./resource-tree-picker";

const ACTION_LABEL: Record<EditAction, string> = {
  read: "読み取り",
  write: "書き込み",
  admin: "管理",
};

/**
 * Editable permission list (#143): existing permissions as removable rows + an add form. Presentational
 * — it calls `onAdd(permissionString)` / `onRemove(permissionString)` and the parent runs the mutation.
 * The resource id is entered raw; the API hashes non-group ids server-side.
 */
export function PermissionEditor({
  permissions,
  busy,
  resolved,
  onAdd,
  onRemove,
}: {
  permissions: string[];
  busy?: boolean;
  /** Optional hashed-id → name resolution (#143); when present, ids render as friendly names. */
  resolved?: ResolvedMap;
  onAdd: (permission: string) => void;
  onRemove: (permission: string) => void;
}) {
  const [resourceType, setResourceType] = useState<EditResourceType>("device");
  const [resourceId, setResourceId] = useState("");
  const [actions, setActions] = useState<EditAction[]>(["read"]);
  const [error, setError] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);

  const toggleAction = (a: EditAction) => {
    setActions((prev) => (prev.includes(a) ? prev.filter((x) => x !== a) : [...prev, a]));
  };

  const handleAdd = () => {
    const result = validatePermissionInput({ resourceId, actions });
    if (!result.ok) {
      setError(result.error);
      return;
    }
    setError(null);
    onAdd(buildPermissionString({ resourceType, resourceId, actions }));
    setResourceId("");
    setActions(["read"]);
  };

  return (
    <div data-testid="permission-editor">
      {permissions.length === 0 ? (
        <p className="text-sm text-gray-600" data-testid="permissions-empty">
          権限はありません
        </p>
      ) : (
        <ul className="flex flex-col gap-1" data-testid="permission-list">
          {permissions.map((perm) => {
            const parsed = parsePermission(perm);
            return (
              <li
                key={perm}
                className="flex items-center gap-2 text-sm"
                data-testid={`permission-row-${perm}`}
              >
                {parsed ? (
                  <>
                    <span
                      className={`rounded px-2 py-0.5 text-xs font-medium ${resourceTypeColor(parsed.resourceType)}`}
                    >
                      {parsed.resourceType}
                    </span>
                    {(() => {
                      const { label, title } = resolveDisplay(parsed.resourceId, resolved);
                      return (
                        <span
                          className={title ? "text-xs text-gray-700" : "font-mono text-xs text-gray-600"}
                          title={title}
                        >
                          {label}
                        </span>
                      );
                    })()}
                    <span className="text-gray-600">
                      {parsed.actions.map(actionLabel).join(" / ")}
                    </span>
                  </>
                ) : (
                  <span className="text-gray-600">{perm}</span>
                )}
                <button
                  type="button"
                  onClick={() => onRemove(perm)}
                  disabled={busy}
                  className="ml-auto rounded border border-red-300 px-2 py-0.5 text-xs text-red-600 hover:bg-red-50 disabled:opacity-50"
                  aria-label={`権限 ${perm} を削除`}
                >
                  削除
                </button>
              </li>
            );
          })}
        </ul>
      )}

      <div className="mt-4 rounded border border-gray-200 p-3" data-testid="permission-add-form">
        <h3 className="mb-2 text-sm font-semibold">権限を追加</h3>
        {error && (
          <p className="mb-2 text-sm text-red-600" data-testid="permission-add-error">
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
            placeholder="リソース ID"
            className="flex-1 rounded border border-gray-300 px-2 py-1 text-sm"
          />
          <button
            type="button"
            onClick={() => setPickerOpen(true)}
            className="rounded border border-gray-300 px-2 py-1 text-sm hover:bg-gray-50"
          >
            ツリーから選択
          </button>
          <div className="flex items-center gap-2">
            {EDIT_ACTIONS.map((a) => (
              <label key={a} className="flex items-center gap-1 text-sm">
                <input
                  type="checkbox"
                  checked={actions.includes(a)}
                  onChange={() => toggleAction(a)}
                />
                {ACTION_LABEL[a]}
              </label>
            ))}
          </div>
          <button
            type="button"
            onClick={handleAdd}
            disabled={busy}
            className="rounded bg-blue-600 px-3 py-1 text-sm text-white hover:bg-blue-700 disabled:opacity-50"
          >
            追加
          </button>
        </div>
        <p className="mt-1 text-xs text-gray-600">
          リソース ID はサーバ側でハッシュ化されます（グループを除く）。
        </p>
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
