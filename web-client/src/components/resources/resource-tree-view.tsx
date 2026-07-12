"use client";

import { resourceTypeColor } from "@/lib/admin/permissions-display";
import { hasChildren } from "@/lib/admin/resource-tree";
import { refKey } from "@/lib/resources/keys";
import type { ResourceTreeLoaders } from "@/lib/resources/tree-loaders";
import type { ResourceRef } from "@/lib/resources/types";
import { useEffect, useRef, useState } from "react";

/**
 * Left-pane resource browser over the digital-twin hierarchy (building→…→point). Lazily loads each
 * level on expand, highlights the selected node, and notifies the parent via `onSelect`. Loaders are
 * injectable for tests; the page wires {@link defaultTreeLoaders}.
 */
export function ResourceTreeView({
  loaders,
  onSelect,
  selectedKey,
  autoExpandBuildingDtId,
}: {
  loaders: ResourceTreeLoaders;
  onSelect: (ref: ResourceRef) => void;
  selectedKey?: string;
  /** When set, the matching building root auto-expands on load (used by search "jump"). */
  autoExpandBuildingDtId?: string;
}) {
  const [roots, setRoots] = useState<ResourceRef[] | null>(null);
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

  if (error) {
    return (
      <p className="text-sm text-red-600" data-testid="tree-error">
        階層の取得に失敗しました: {error}
      </p>
    );
  }
  if (roots === null)
    return <p className="text-sm text-gray-600">読み込み中…</p>;
  if (roots.length === 0) {
    return (
      <p className="text-sm text-gray-600" data-testid="tree-empty">
        建物がありません
      </p>
    );
  }

  return (
    <ul>
      {roots.map((node) => (
        <TreeNode
          key={refKey(node)}
          node={node}
          loadChildren={loaders.loadChildren}
          onSelect={onSelect}
          selectedKey={selectedKey}
          defaultExpanded={
            node.type === "building" && node.dtId === autoExpandBuildingDtId
          }
        />
      ))}
    </ul>
  );
}

function TreeNode({
  node,
  loadChildren,
  onSelect,
  selectedKey,
  defaultExpanded = false,
}: {
  node: ResourceRef;
  loadChildren: ResourceTreeLoaders["loadChildren"];
  onSelect: (ref: ResourceRef) => void;
  selectedKey?: string;
  defaultExpanded?: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const [children, setChildren] = useState<ResourceRef[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const expandable = hasChildren(node.type);
  const selected = selectedKey === refKey(node);
  const mounted = useRef(true);

  const load = () => {
    if (children !== null || loading) return;
    setLoading(true);
    setError(null);
    loadChildren(node)
      .then((c) => {
        if (mounted.current) setChildren(c);
      })
      .catch((e: Error) => {
        if (mounted.current) setError(e.message);
      })
      .finally(() => {
        if (mounted.current) setLoading(false);
      });
  };

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  // Auto-expand once (e.g. search "jump" reveals a building).
  useEffect(() => {
    if (defaultExpanded && expandable) {
      setExpanded(true);
      load();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [defaultExpanded]);

  const toggle = () => {
    const next = !expanded;
    setExpanded(next);
    if (next) load();
  };

  return (
    <li className="ml-2">
      <div
        aria-current={selected ? "true" : undefined}
        className={`flex items-center gap-2 rounded py-1 ${selected ? "bg-blue-50" : ""}`}
      >
        {expandable ? (
          <button
            type="button"
            onClick={toggle}
            aria-label={
              expanded ? `${node.name} を折りたたむ` : `${node.name} を展開`
            }
            className="flex h-5 w-5 items-center justify-center rounded text-gray-500 hover:bg-gray-100"
          >
            {loading ? "…" : expanded ? "▼" : "▶"}
          </button>
        ) : (
          <span className="inline-block w-5" />
        )}
        <span
          className={`rounded px-2 py-0.5 text-xs font-medium ${resourceTypeColor(node.type)}`}
        >
          {node.type}
        </span>
        <button
          type="button"
          onClick={() => onSelect(node)}
          className="flex-1 truncate text-left text-sm hover:underline"
          title={node.name}
        >
          {node.name || node.id}
        </button>
      </div>
      {expanded && (
        <div className="ml-3 border-l border-gray-200">
          {error ? (
            <p className="ml-3 py-1 text-xs text-red-600">{error}</p>
          ) : children === null ? (
            !loading && null
          ) : children.length === 0 ? (
            <p className="ml-3 py-1 text-xs text-gray-600">
              子要素がありません
            </p>
          ) : (
            <ul>
              {children.map((child) => (
                <TreeNode
                  key={refKey(child)}
                  node={child}
                  loadChildren={loadChildren}
                  onSelect={onSelect}
                  selectedKey={selectedKey}
                />
              ))}
            </ul>
          )}
        </div>
      )}
    </li>
  );
}
