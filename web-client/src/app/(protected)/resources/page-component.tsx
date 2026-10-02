"use client";

import { MetadataEditor } from "@/components/resources/metadata-editor";
import { ResourceDetail } from "@/components/resources/resource-detail";
import { ResourceSearchBox } from "@/components/resources/resource-search-box";
import { ResourceTreeView } from "@/components/resources/resource-tree-view";
import { parseAuthClaims } from "@/lib/auth/claims";
import { OIDC_TOKEN_COOKIE } from "@/lib/auth/oidc-config";
import { parseRefKey, refKey } from "@/lib/resources/keys";
import {
  fetchResourceMetadata,
  resolveRef,
  updateResourceMetadata,
} from "@/lib/resources/repository";
import {
  filtersFromParams,
  filtersToParams,
  type SearchFilters,
} from "@/lib/resources/search-filters";
import { defaultTreeLoaders } from "@/lib/resources/tree-loaders";
import type {
  ResourceMetadata,
  ResourceMetadataPatch,
  ResourceRef,
  SearchHit,
} from "@/lib/resources/types";
import Cookies from "js-cookie";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";

/**
 * Resource explorer: left = incremental search + lazy-expand tree, right = selected-node detail.
 * The selection is mirrored to the `?sel=<type>:<id>` query param for deep-linking and reload.
 */
export default function ResourcesPageComponent() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const sel = searchParams.get("sel");
  // The search form starts from the URL (a shared / reloaded filtered view) and writes back to it.
  const [initialFilters] = useState(() => filtersFromParams(searchParams));

  const [selected, setSelected] = useState<ResourceRef | null>(null);
  const [autoExpandBuildingDtId, setAutoExpandBuildingDtId] = useState<
    string | undefined
  >(undefined);
  const [metadata, setMetadata] = useState<ResourceMetadata | undefined>(undefined);
  const [editingMetadata, setEditingMetadata] = useState(false);

  const isAdmin =
    parseAuthClaims(Cookies.get(OIDC_TOKEN_COOKIE) ?? null).role === "admin";

  // Hydrate the right pane from the URL on first load / when sel changes externally.
  useEffect(() => {
    if (!sel) {
      setSelected(null);
      setMetadata(undefined);
      return;
    }
    if (selected && refKey(selected) === sel) return;
    const parsed = parseRefKey(sel);
    if (!parsed) return;
    let active = true;
    resolveRef(parsed.type, parsed.id).then((ref) => {
      if (active && ref) setSelected(ref);
    });
    return () => {
      active = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sel]);

  // Fetch metadata whenever the selected resource changes.
  useEffect(() => {
    if (!selected) {
      setMetadata(undefined);
      setEditingMetadata(false);
      return;
    }
    let active = true;
    setMetadata(undefined);
    setEditingMetadata(false);
    // Points use logical pointId in the URL (matching PointController convention); all others use dtId.
    const id = selected.type === "point" ? selected.id : selected.dtId;
    fetchResourceMetadata(selected.type, id).then((m) => {
      if (active) setMetadata(m);
    });
    return () => {
      active = false;
    };
  }, [selected]);

  // Bumped by every selection so a slow health-hit lookup cannot overwrite a newer choice.
  const selectionSeq = useRef(0);

  const select = useCallback(
    (ref: ResourceRef) => {
      selectionSeq.current += 1;
      setSelected(ref);
      const params = new URLSearchParams(searchParams.toString());
      params.set("sel", refKey(ref));
      router.replace(`/resources?${params.toString()}`);
    },
    [router, searchParams],
  );

  // Mirror the search form to the URL. Reads the live query string rather than the hook's value so a
  // keystroke right after a selection cannot overwrite `sel` with a stale copy.
  const syncFilters = useCallback(
    (filters: SearchFilters) => {
      const next = filtersToParams(filters, new URLSearchParams(window.location.search));
      const qs = next.toString();
      router.replace(qs ? `/resources?${qs}` : "/resources", { scroll: false });
    },
    [router],
  );

  const pickFromSearch = useCallback(
    (hit: SearchHit) => {
      // A health-routed hit has no digital-twin id (the health row does not carry one): resolve the real
      // node rather than showing the business id as if it were the twin's.
      if (!hit.dtId) {
        const seq = ++selectionSeq.current;
        resolveRef(hit.type, hit.id).then((ref) => {
          if (ref && seq === selectionSeq.current) select(ref);
        });
        return;
      }
      select({ type: hit.type, dtId: hit.dtId, id: hit.id, name: hit.name });
      // Reveal the hit's building in the tree when known (best-effort jump).
      if (hit.buildingDtId) setAutoExpandBuildingDtId(hit.buildingDtId);
      else if (hit.type === "building") setAutoExpandBuildingDtId(hit.dtId);
    },
    [select],
  );

  async function handleSaveMetadata(patch: ResourceMetadataPatch) {
    // Capture selection at call time; guard against navigation mid-flight.
    const snapshot = selected;
    if (!snapshot) return;
    const id = snapshot.type === "point" ? snapshot.id : snapshot.dtId;
    await updateResourceMetadata(snapshot.type, id, patch);
    const updated = await fetchResourceMetadata(snapshot.type, id);
    // Only apply the result if the user hasn't navigated to a different resource.
    if (selected === snapshot) {
      setMetadata(updated);
      setEditingMetadata(false);
    }
  }

  return (
    <div className="container mx-auto px-4 py-6">
      <h1 className="mb-4 text-2xl font-bold">リソース</h1>
      {/* Stacked until `lg`, side-by-side two-pane from `lg` up (#199 UX-10). The split is NOT at
          `md`: the shell sidebar also turns static at `md` (224px), so at 768px the content area is
          only ~544px — a two-pane there crushes the detail pane to ~210px (#208 review). `lg` is the
          first width where the sidebar-reduced content is wide enough for a readable detail pane. */}
      <div data-testid="resource-two-pane" className="flex flex-col gap-4 lg:flex-row">
        <aside className="w-full rounded-lg border border-gray-200 bg-white p-3 lg:w-1/3 lg:min-w-[18rem]">
          <ResourceSearchBox
            onPick={pickFromSearch}
            initialFilters={initialFilters}
            onFiltersChange={syncFilters}
          />
          <div className="mt-3 max-h-[70vh] overflow-auto border-t border-gray-100 pt-3">
            <ResourceTreeView
              loaders={defaultTreeLoaders}
              onSelect={select}
              selectedKey={selected ? refKey(selected) : undefined}
              autoExpandBuildingDtId={autoExpandBuildingDtId}
            />
          </div>
        </aside>
        <section className="min-h-[60vh] flex-1 rounded-lg border border-gray-200 bg-white p-4">
          {editingMetadata && selected && metadata ? (
            <div>
              <h2 className="mb-3 text-base font-semibold">
                メタデータ編集 — {selected.name || selected.id}
              </h2>
              <MetadataEditor
                metadata={metadata}
                onSave={handleSaveMetadata}
                onCancel={() => setEditingMetadata(false)}
              />
            </div>
          ) : (
            <ResourceDetail
              resource={selected}
              metadata={metadata}
              canWrite={isAdmin}
              onEditMetadata={() => setEditingMetadata(true)}
            />
          )}
        </section>
      </div>
    </div>
  );
}
