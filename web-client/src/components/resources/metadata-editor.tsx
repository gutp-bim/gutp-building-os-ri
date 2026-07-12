"use client";

import type { ResourceMetadata, ResourceMetadataPatch } from "@/lib/resources/types";
import { useState } from "react";

type IdentEntry = { key: string; value: string; deleted: boolean };
type TagEntry = { key: string; value: boolean; deleted: boolean };

function initIdents(m: ResourceMetadata): IdentEntry[] {
  return Object.entries(m.identifiers).map(([key, value]) => ({
    key,
    value,
    deleted: false,
  }));
}

function initTags(m: ResourceMetadata): TagEntry[] {
  return Object.entries(m.customTags).map(([key, value]) => ({
    key,
    value,
    deleted: false,
  }));
}

/** Inline editor for identifiers and customTags. Tracks deletions as null in the patch. */
export function MetadataEditor({
  metadata,
  onSave,
  onCancel,
}: {
  metadata: ResourceMetadata;
  onSave: (patch: ResourceMetadataPatch) => Promise<void>;
  onCancel: () => void;
}) {
  const [idents, setIdents] = useState<IdentEntry[]>(initIdents(metadata));
  const [tags, setTags] = useState<TagEntry[]>(initTags(metadata));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function buildPatch(): ResourceMetadataPatch {
    const originalIdentKeys = new Set(Object.keys(metadata.identifiers));
    const originalTagKeys = new Set(Object.keys(metadata.customTags));

    const identifiers: Record<string, string | null> = {};
    for (const e of idents) {
      if (e.deleted) {
        if (originalIdentKeys.has(e.key)) identifiers[e.key] = null;
      } else {
        if (!e.key.trim()) continue;
        identifiers[e.key] = e.value;
      }
    }

    const customTags: Record<string, boolean | null> = {};
    for (const e of tags) {
      if (e.deleted) {
        if (originalTagKeys.has(e.key)) customTags[e.key] = null;
      } else {
        if (!e.key.trim()) continue;
        customTags[e.key] = e.value;
      }
    }

    return { identifiers, customTags };
  }

  async function handleSave() {
    setSaving(true);
    setError(null);
    try {
      await onSave(buildPatch());
    } catch (e) {
      setError(e instanceof Error ? e.message : "保存に失敗しました");
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="space-y-4 text-sm" data-testid="metadata-editor">
      {/* identifiers */}
      <section>
        <div className="mb-1 flex items-center justify-between">
          <p className="text-xs font-medium text-gray-700">identifiers</p>
          <button
            type="button"
            className="rounded bg-gray-100 px-2 py-0.5 text-xs hover:bg-gray-200"
            data-testid="add-ident-btn"
            onClick={() =>
              setIdents((prev) => [...prev, { key: "", value: "", deleted: false }])
            }
          >
            + 追加
          </button>
        </div>
        <div className="space-y-1">
          {idents.map((e, i) =>
            e.deleted ? null : (
              <div key={i} className="flex gap-1">
                <input
                  className="w-1/3 rounded border px-1 py-0.5 text-xs"
                  placeholder="key"
                  value={e.key}
                  onChange={(ev) =>
                    setIdents((prev) =>
                      prev.map((x, j) => (j === i ? { ...x, key: ev.target.value } : x)),
                    )
                  }
                />
                <input
                  className="flex-1 rounded border px-1 py-0.5 text-xs font-mono"
                  placeholder="value"
                  value={e.value}
                  onChange={(ev) =>
                    setIdents((prev) =>
                      prev.map((x, j) => (j === i ? { ...x, value: ev.target.value } : x)),
                    )
                  }
                />
                <button
                  type="button"
                  className="rounded px-1 text-xs text-red-500 hover:bg-red-50"
                  data-testid="delete-ident-row"
                  onClick={() =>
                    setIdents((prev) =>
                      prev.map((x, j) => (j === i ? { ...x, deleted: true } : x)),
                    )
                  }
                >
                  ×
                </button>
              </div>
            ),
          )}
        </div>
      </section>

      {/* customTags */}
      <section>
        <div className="mb-1 flex items-center justify-between">
          <p className="text-xs font-medium text-gray-700">customTags</p>
          <button
            type="button"
            className="rounded bg-gray-100 px-2 py-0.5 text-xs hover:bg-gray-200"
            data-testid="add-tag-btn"
            onClick={() =>
              setTags((prev) => [...prev, { key: "", value: false, deleted: false }])
            }
          >
            + 追加
          </button>
        </div>
        <div className="space-y-1">
          {tags.map((e, i) =>
            e.deleted ? null : (
              <div key={i} className="flex items-center gap-1">
                <input
                  className="w-1/3 rounded border px-1 py-0.5 text-xs"
                  placeholder="key"
                  value={e.key}
                  onChange={(ev) =>
                    setTags((prev) =>
                      prev.map((x, j) => (j === i ? { ...x, key: ev.target.value } : x)),
                    )
                  }
                />
                <input
                  type="checkbox"
                  checked={e.value}
                  onChange={(ev) =>
                    setTags((prev) =>
                      prev.map((x, j) => (j === i ? { ...x, value: ev.target.checked } : x)),
                    )
                  }
                />
                <button
                  type="button"
                  className="rounded px-1 text-xs text-red-500 hover:bg-red-50"
                  data-testid="delete-tag-row"
                  onClick={() =>
                    setTags((prev) =>
                      prev.map((x, j) => (j === i ? { ...x, deleted: true } : x)),
                    )
                  }
                >
                  ×
                </button>
              </div>
            ),
          )}
        </div>
      </section>

      {error && <p className="text-xs text-red-600">{error}</p>}

      <div className="flex gap-2">
        <button
          type="button"
          className="rounded bg-blue-600 px-3 py-1 text-xs text-white hover:bg-blue-700 disabled:opacity-50"
          data-testid="metadata-save-btn"
          disabled={saving}
          onClick={handleSave}
        >
          {saving ? "保存中…" : "保存"}
        </button>
        <button
          type="button"
          className="rounded bg-gray-100 px-3 py-1 text-xs hover:bg-gray-200"
          data-testid="metadata-cancel-btn"
          onClick={onCancel}
        >
          キャンセル
        </button>
      </div>
    </div>
  );
}
