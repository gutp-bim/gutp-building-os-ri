"use client";

import { ResourceDetail } from "@/components/resources/resource-detail";
import { ResourceSearchBox } from "@/components/resources/resource-search-box";
import { ResourceTreeView } from "@/components/resources/resource-tree-view";
import { parseRefKey, refKey } from "@/lib/resources/keys";
import { resolveRef } from "@/lib/resources/repository";
import { defaultTreeLoaders } from "@/lib/resources/tree-loaders";
import type { ResourceRef, SearchHit } from "@/lib/resources/types";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";

/**
 * Resource explorer: left = incremental search + lazy-expand tree, right = selected-node detail.
 * The selection is mirrored to the `?sel=<type>:<id>` query param for deep-linking and reload.
 */
export default function ResourcesPageComponent() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const sel = searchParams.get("sel");

  const [selected, setSelected] = useState<ResourceRef | null>(null);
  const [autoExpandBuildingDtId, setAutoExpandBuildingDtId] = useState<
    string | undefined
  >(undefined);

  // Hydrate the right pane from the URL on first load / when sel changes externally.
  useEffect(() => {
    if (!sel) {
      setSelected(null);
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

  const select = useCallback(
    (ref: ResourceRef) => {
      setSelected(ref);
      const params = new URLSearchParams(searchParams.toString());
      params.set("sel", refKey(ref));
      router.replace(`/resources?${params.toString()}`);
    },
    [router, searchParams],
  );

  const pickFromSearch = useCallback(
    (hit: SearchHit) => {
      select({ type: hit.type, dtId: hit.dtId, id: hit.id, name: hit.name });
      // Reveal the hit's building in the tree when known (best-effort jump).
      if (hit.buildingDtId) setAutoExpandBuildingDtId(hit.buildingDtId);
      else if (hit.type === "building") setAutoExpandBuildingDtId(hit.dtId);
    },
    [select],
  );

  return (
    <div className="container mx-auto px-4 py-6">
      <h1 className="mb-4 text-2xl font-bold">リソース</h1>
      <div className="flex gap-4">
        <aside className="w-1/3 min-w-[18rem] rounded-lg border border-gray-200 bg-white p-3">
          <ResourceSearchBox onPick={pickFromSearch} />
          <div className="mt-3 max-h-[70vh] overflow-auto border-t border-gray-100 pt-3">
            <ResourceTreeView
              loaders={defaultTreeLoaders}
              onSelect={select}
              selectedKey={selected ? refKey(selected) : undefined}
              autoExpandBuildingDtId={autoExpandBuildingDtId}
            />
          </div>
        </aside>
        <section className="min-h-[60vh] flex-1 rounded-lg border border-gray-200 bg-white">
          <ResourceDetail resource={selected} />
        </section>
      </div>
    </div>
  );
}
