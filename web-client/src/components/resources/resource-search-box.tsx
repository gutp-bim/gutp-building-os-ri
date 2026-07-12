"use client";

import { resourceTypeColor } from "@/lib/admin/permissions-display";
import { searchResources } from "@/lib/resources/repository";
import { normalizeTags } from "@/lib/resources/search";
import type { ResourceType, SearchHit } from "@/lib/resources/types";
import { useEffect, useRef, useState } from "react";

const TYPE_OPTIONS: { value: "" | ResourceType; label: string }[] = [
  { value: "", label: "すべて" },
  { value: "building", label: "建物" },
  { value: "floor", label: "フロア" },
  { value: "space", label: "スペース" },
  { value: "device", label: "デバイス" },
  { value: "point", label: "ポイント" },
];

const DEBOUNCE_MS = 300;

/**
 * Incremental cross-resource search. Debounced query + a type filter + SBCO customTags chips (#332);
 * multiple tags are ANDed (`customTags[key] == true`). A search runs when there is a query term OR at
 * least one tag. Results are clickable and call `onPick(hit)`. The search function is injectable for
 * tests; it defaults to the repository façade.
 */
export function ResourceSearchBox({
  onPick,
  search = searchResources,
}: {
  onPick: (hit: SearchHit) => void;
  search?: (params: {
    q?: string;
    type?: ResourceType;
    tags?: string[];
  }) => Promise<SearchHit[]>;
}) {
  const [q, setQ] = useState("");
  const [type, setType] = useState<"" | ResourceType>("");
  const [tags, setTags] = useState<string[]>([]);
  const [tagDraft, setTagDraft] = useState("");
  const [hits, setHits] = useState<SearchHit[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const addTag = (raw: string) => {
    const [t] = normalizeTags([raw]);
    if (!t) return;
    setTags((prev) => (prev.includes(t) ? prev : [...prev, t]));
    setTagDraft("");
  };
  const removeTag = (t: string) => setTags((prev) => prev.filter((x) => x !== t));

  useEffect(() => {
    const term = q.trim();
    // A search needs at least a query term or one tag; otherwise clear results.
    if (!term && tags.length === 0) {
      setHits(null);
      setError(null);
      return;
    }
    const handle = setTimeout(() => {
      setLoading(true);
      setError(null);
      search({ q: term || undefined, type: type || undefined, tags: tags.length > 0 ? tags : undefined })
        .then((r) => {
          if (mounted.current) setHits(r);
        })
        .catch((e: Error) => {
          if (mounted.current) setError(e.message);
        })
        .finally(() => {
          if (mounted.current) setLoading(false);
        });
    }, DEBOUNCE_MS);
    return () => clearTimeout(handle);
  }, [q, type, tags, search]);

  return (
    <div data-testid="resource-search-box">
      <div className="flex gap-2">
        <input
          type="search"
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder="名前・IDで検索"
          aria-label="リソース検索"
          className="min-w-0 flex-1 rounded border border-gray-300 px-2 py-1 text-sm"
        />
        <select
          value={type}
          onChange={(e) => setType(e.target.value as "" | ResourceType)}
          aria-label="種別で絞り込み"
          className="rounded border border-gray-300 px-1 py-1 text-sm"
        >
          {TYPE_OPTIONS.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      </div>

      {/* customTags chips (AND). Add on Enter, remove with ×. */}
      <div className="mt-2">
        <input
          type="text"
          value={tagDraft}
          onChange={(e) => setTagDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              addTag(tagDraft);
            }
          }}
          placeholder="タグを追加（Enter、複数=AND）"
          aria-label="タグで絞り込み"
          className="w-full rounded border border-gray-300 px-2 py-1 text-sm"
          data-testid="tag-input"
        />
        {tags.length > 0 && (
          <ul className="mt-1 flex flex-wrap gap-1" data-testid="tag-chips">
            {tags.map((t) => (
              <li key={t}>
                <button
                  type="button"
                  onClick={() => removeTag(t)}
                  className="inline-flex items-center gap-1 rounded bg-blue-100 px-1.5 py-0.5 text-xs text-blue-800 hover:bg-blue-200"
                  aria-label={`タグ ${t} を削除`}
                  data-testid={`tag-chip-${t}`}
                >
                  {t}<span aria-hidden>×</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      {loading && <p className="mt-2 text-xs text-gray-600">検索中…</p>}
      {error && (
        <p className="mt-2 text-xs text-red-600">検索に失敗しました: {error}</p>
      )}
      {hits !== null && !loading && hits.length === 0 && (
        <p className="mt-2 text-xs text-gray-600" data-testid="search-empty">
          該当なし
        </p>
      )}
      {hits !== null && hits.length > 0 && (
        <ul className="mt-2 max-h-60 overflow-auto rounded border border-gray-200">
          {hits.map((h, i) => (
            // index keeps keys unique even if ids/dtIds contain colons (urn:...) and collide
            <li key={`${i}:${h.type}:${h.dtId}:${h.id}`}>
              <button
                type="button"
                onClick={() => onPick(h)}
                className="flex w-full items-center gap-2 px-2 py-1 text-left text-sm hover:bg-gray-50"
              >
                <span
                  className={`rounded px-1.5 py-0.5 text-xs font-medium ${resourceTypeColor(h.type)}`}
                >
                  {h.type}
                </span>
                <span className="flex-1 truncate" title={h.name}>
                  {h.name || h.id}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
