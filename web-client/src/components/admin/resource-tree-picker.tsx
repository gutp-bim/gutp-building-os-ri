"use client";

import { useEffect, useRef, useState } from "react";
import { resourceTypeColor } from "@/lib/admin/permissions-display";
import { fetchBuildings, fetchChildren } from "@/lib/admin/fetch-hierarchy";
import { hasChildren, type TreeNodeData, type TreeResourceType } from "@/lib/admin/resource-tree";

export type TreeLoaders = {
  loadRoots: (signal?: AbortSignal) => Promise<TreeNodeData[]>;
  loadChildren: (
    type: TreeResourceType,
    id: string,
    signal?: AbortSignal,
  ) => Promise<TreeNodeData[]>;
};

const DEFAULT_LOADERS: TreeLoaders = { loadRoots: fetchBuildings, loadChildren: fetchChildren };

/**
 * Modal tree-browse picker over the digital-twin hierarchy (#143). Lazily loads each level on expand
 * and calls `onSelect(type, id, name)` when a node is chosen. Loaders are injectable for testing;
 * they default to the real bespoke-fetch hierarchy clients.
 */
export function ResourceTreePicker({
  onSelect,
  onClose,
  loaders = DEFAULT_LOADERS,
}: {
  onSelect: (type: TreeResourceType, id: string, name: string) => void;
  onClose: () => void;
  loaders?: TreeLoaders;
}) {
  const [roots, setRoots] = useState<TreeNodeData[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    loaders
      .loadRoots(controller.signal)
      .then((r) => {
        if (mounted.current) setRoots(r);
      })
      .catch((e: Error) => {
        if (e.name !== "AbortError" && mounted.current) setError(e.message);
      });
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, [loaders]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50" data-testid="resource-tree-picker">
      <div className="flex max-h-[80vh] w-full max-w-xl flex-col rounded-lg bg-white shadow-xl">
        <div className="flex items-center justify-between border-b p-4">
          <h2 className="text-lg font-semibold">リソースを選択</h2>
          <button
            type="button"
            onClick={onClose}
            aria-label="閉じる"
            className="text-2xl leading-none text-gray-500 hover:text-gray-700"
          >
            ×
          </button>
        </div>
        <div className="flex-1 overflow-auto p-4">
          {error ? (
            <p className="text-sm text-red-600" data-testid="tree-error">
              階層の取得に失敗しました: {error}
            </p>
          ) : roots === null ? (
            <p className="text-sm text-gray-500">読み込み中…</p>
          ) : roots.length === 0 ? (
            <p className="text-sm text-gray-500" data-testid="tree-empty">
              建物がありません
            </p>
          ) : (
            <ul>
              {roots.map((node) => (
                <TreeNode
                  key={`${node.type}:${node.id}`}
                  node={node}
                  loadChildren={loaders.loadChildren}
                  onSelect={onSelect}
                />
              ))}
            </ul>
          )}
        </div>
      </div>
    </div>
  );
}

function TreeNode({
  node,
  loadChildren,
  onSelect,
}: {
  node: TreeNodeData;
  loadChildren: TreeLoaders["loadChildren"];
  onSelect: (type: TreeResourceType, id: string, name: string) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const [children, setChildren] = useState<TreeNodeData[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const expandable = hasChildren(node.type);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const toggle = () => {
    const next = !expanded;
    setExpanded(next);
    if (next && children === null && !loading) {
      setLoading(true);
      setError(null);
      loadChildren(node.type, node.id)
        .then((c) => {
          if (mounted.current) setChildren(c);
        })
        .catch((e: Error) => {
          if (mounted.current) setError(e.message);
        })
        .finally(() => {
          if (mounted.current) setLoading(false);
        });
    }
  };

  return (
    <li className="ml-2">
      <div className="flex items-center gap-2 py-1">
        {expandable ? (
          <button
            type="button"
            onClick={toggle}
            aria-label={expanded ? `${node.name} を折りたたむ` : `${node.name} を展開`}
            className="flex h-5 w-5 items-center justify-center rounded text-gray-500 hover:bg-gray-100"
          >
            {loading ? "…" : expanded ? "▼" : "▶"}
          </button>
        ) : (
          <span className="inline-block w-5" />
        )}
        <span className={`rounded px-2 py-0.5 text-xs font-medium ${resourceTypeColor(node.type)}`}>
          {node.type}
        </span>
        <span className="flex-1 truncate text-sm" title={node.name}>
          {node.name || node.id}
        </span>
        <button
          type="button"
          onClick={() => onSelect(node.type, node.id, node.name)}
          className="rounded bg-blue-600 px-2 py-0.5 text-xs text-white hover:bg-blue-700"
        >
          選択
        </button>
      </div>
      {expanded && (
        <div className="ml-3 border-l border-gray-200">
          {error ? (
            <p className="ml-3 py-1 text-xs text-red-600">{error}</p>
          ) : children === null ? (
            !loading && null
          ) : children.length === 0 ? (
            <p className="ml-3 py-1 text-xs text-gray-500">子要素がありません</p>
          ) : (
            <ul>
              {children.map((child) => (
                <TreeNode
                  key={`${child.type}:${child.id}`}
                  node={child}
                  loadChildren={loadChildren}
                  onSelect={onSelect}
                />
              ))}
            </ul>
          )}
        </div>
      )}
    </li>
  );
}
