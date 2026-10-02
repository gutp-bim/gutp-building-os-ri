"use client";

import { resourceTypeColor } from "@/lib/admin/permissions-display";
import type { TagSuggestion } from "@/lib/resources/repository";
import { fetchFacetsWithHealth, searchResourcesOrHealth } from "@/lib/resources/search-dispatch";
import { normalizeTags } from "@/lib/resources/search";
import {
  EMPTY_FILTERS,
  ATTRIBUTE_GROUPS,
  hasCriteria,
  toggleValue,
  toSearchParams,
  type FacetGroup,
  type SearchFilters,
} from "@/lib/resources/search-filters";
import type {
  ResourceFacetsResult,
  ResourceType,
  SearchHit,
  SearchParams,
} from "@/lib/resources/types";
import { useEffect, useRef, useState } from "react";
import { ResourceFacetPanel } from "./resource-facet-panel";
import { TagSuggestInput } from "./tag-suggest-input";

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
 * Incremental cross-resource search. Debounced query + a type filter + SBCO customTags chips (#332;
 * multiple tags are ANDed) + structured-attribute facets (#454: equipment / measurement / unit /
 * gateway, ORed within a group). A search runs when any criterion is set. The filters can start from
 * (`initialFilters`) and be mirrored to (`onFiltersChange`) the URL. Results are clickable and call
 * `onPick(hit)`. The data functions are injectable for tests; they default to the repository façade.
 */
export function ResourceSearchBox({
  onPick,
  search = searchResourcesOrHealth,
  loadFacets = fetchFacetsWithHealth,
  suggestTags,
  initialFilters = EMPTY_FILTERS,
  onFiltersChange,
}: {
  onPick: (hit: SearchHit) => void;
  search?: (params: SearchParams) => Promise<SearchHit[]>;
  loadFacets?: (params: SearchParams) => Promise<ResourceFacetsResult>;
  /** Tag candidates for the tag input; defaults to the repository façade. */
  suggestTags?: (prefix: string) => Promise<TagSuggestion[]>;
  initialFilters?: SearchFilters;
  onFiltersChange?: (filters: SearchFilters) => void;
}) {
  const [filters, setFilters] = useState<SearchFilters>(initialFilters);
  const [tagDraft, setTagDraft] = useState("");
  const [hits, setHits] = useState<SearchHit[] | null>(null);
  const [facets, setFacets] = useState<ResourceFacetsResult | null>(null);
  const [showFacets, setShowFacets] = useState(hasCriteria(initialFilters));
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const update = (patch: Partial<SearchFilters>) => {
    const next = { ...filters, ...patch };
    setFilters(next);
    onFiltersChange?.(next);
  };

  const { q, type, tags } = filters;

  const addTag = (raw: string) => {
    const [t] = normalizeTags([raw]);
    if (!t) return;
    if (!tags.includes(t)) update({ tags: [...tags, t] });
    setTagDraft("");
  };
  const removeTag = (t: string) => update({ tags: tags.filter((x) => x !== t) });
  // Data health (Freshness / Alarm, Points only) and the asset attributes are answered by different
  // APIs and are not combined: choosing one clears the other, so what is checked is always what ran.
  const toggleFacet = (group: FacetGroup, value: string) => {
    const next = { ...filters, [group]: toggleValue(filters[group], value) } as SearchFilters;
    if (group === "freshness" || group === "alarm") {
      next.type = "point";
      for (const g of ATTRIBUTE_GROUPS) next[g] = [];
    } else {
      next.freshness = [];
      next.alarm = [];
    }
    update(next);
  };
  // Health is a Point-only notion; leaving Points drops it.
  const selectType = (t: "" | ResourceType) =>
    update(t === "point" ? { type: t } : { type: t, freshness: [], alarm: [] });

  const searching = hasCriteria(filters);

  useEffect(() => {
    // A search needs at least one criterion; otherwise clear the results.
    if (!searching) {
      setHits(null);
      setError(null);
      return;
    }
    const handle = setTimeout(() => {
      setLoading(true);
      setError(null);
      search(toSearchParams(filters))
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filters, search]);

  // Starting a search opens the facet panel (once per start — hiding it afterwards sticks).
  useEffect(() => {
    if (searching) setShowFacets(true);
  }, [searching]);

  // Facet counts follow the same filters. A failed lookup just hides the panel — the search itself works.
  useEffect(() => {
    if (!showFacets) {
      setFacets(null);
      return;
    }
    let cancelled = false;
    const handle = setTimeout(() => {
      loadFacets(toSearchParams(filters))
        .then((f) => {
          if (!cancelled && mounted.current) setFacets(f);
        })
        .catch(() => {
          if (!cancelled && mounted.current) setFacets(null);
        });
    }, DEBOUNCE_MS);
    return () => {
      cancelled = true;
      clearTimeout(handle);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filters, showFacets, loadFacets]);

  return (
    <div data-testid="resource-search-box">
      <div className="flex gap-2">
        <input
          type="search"
          value={q}
          onChange={(e) => update({ q: e.target.value })}
          placeholder="名前・IDで検索"
          aria-label="リソース検索"
          className="min-w-0 flex-1 rounded border border-gray-300 px-2 py-1 text-sm"
        />
        <select
          value={type}
          onChange={(e) => selectType(e.target.value as "" | ResourceType)}
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
        <TagSuggestInput
          value={tagDraft}
          onChange={setTagDraft}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              addTag(tagDraft);
            }
          }}
          placeholder="タグを追加（Enter、複数=AND）"
          ariaLabel="タグで絞り込み"
          className="w-full rounded border border-gray-300 px-2 py-1 text-sm"
          data-testid="tag-input"
          suggest={suggestTags}
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

      <div className="mt-2">
        <button
          type="button"
          onClick={() => setShowFacets((v) => !v)}
          aria-expanded={showFacets}
          className="text-xs text-blue-700 hover:underline"
          data-testid="facet-toggle"
        >
          {showFacets ? "絞り込み項目を隠す" : "絞り込み項目を表示"}
        </button>
        {showFacets && (
          <ResourceFacetPanel
            facets={facets}
            filters={filters}
            onToggle={toggleFacet}
            onSelectType={selectType}
          />
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
