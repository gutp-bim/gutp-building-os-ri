"use client";

import type { FacetGroup, SearchFilters } from "@/lib/resources/search-filters";
import type { FacetValue, ResourceFacetsResult, ResourceType } from "@/lib/resources/types";

const TYPE_LABEL: Record<ResourceType, string> = {
  building: "建物",
  floor: "フロア",
  space: "スペース",
  device: "デバイス",
  point: "ポイント",
};

const GROUPS: {
  group: FacetGroup;
  facet: keyof Pick<ResourceFacetsResult, "deviceTypes" | "pointTypes" | "units" | "gateways">;
  label: string;
}[] = [
  { group: "deviceTypes", facet: "deviceTypes", label: "機器" },
  { group: "pointTypes", facet: "pointTypes", label: "計測" },
  { group: "units", facet: "units", label: "単位" },
  { group: "gatewayIds", facet: "gateways", label: "Gateway" },
];

/** The facet's values plus any selected value it no longer matches (count 0), so it can be unchecked. */
function withSelected(values: FacetValue[], selected: readonly string[]): FacetValue[] {
  const known = new Set(values.map((v) => v.value));
  return [...values, ...selected.filter((s) => !known.has(s)).map((value) => ({ value, count: 0 }))];
}

function FacetRow({
  label,
  count,
  checked,
  onChange,
  testId,
}: {
  label: string;
  count: number;
  checked: boolean;
  onChange: () => void;
  testId: string;
}) {
  const text = `${label}（${count}）`;
  return (
    <li>
      <label className="flex cursor-pointer items-center gap-1.5 text-xs">
        <input type="checkbox" checked={checked} onChange={onChange} aria-label={text} data-testid={testId} />
        <span className="truncate" title={label}>
          {text}
        </span>
      </label>
    </li>
  );
}

/**
 * Facet checkboxes for the resource search (#454): resource type (single choice) and the structured
 * attributes — equipment, measurement, unit, gateway (multi, ORed within a group). Counts come from the
 * server and cover only what the caller can read; a selected value stays listed so it can be cleared.
 */
export function ResourceFacetPanel({
  facets,
  filters,
  onToggle,
  onSelectType,
}: {
  facets: ResourceFacetsResult | null;
  filters: SearchFilters;
  onToggle: (group: FacetGroup, value: string) => void;
  onSelectType: (type: "" | ResourceType) => void;
}) {
  if (!facets) return null;

  const types = withSelected(facets.types, filters.type ? [filters.type] : []);

  return (
    <div className="mt-2 space-y-2 rounded border border-gray-200 p-2" data-testid="facet-panel">
      {types.length > 0 && (
        <fieldset>
          <legend className="text-xs font-medium text-gray-700">種別</legend>
          <ul className="mt-1 space-y-0.5">
            {types.map((t) => (
              <FacetRow
                key={t.value}
                label={TYPE_LABEL[t.value as ResourceType] ?? t.value}
                count={t.count}
                checked={filters.type === t.value}
                onChange={() => onSelectType(filters.type === t.value ? "" : (t.value as ResourceType))}
                testId={`facet-type-${t.value}`}
              />
            ))}
          </ul>
        </fieldset>
      )}
      {GROUPS.map(({ group, facet, label }) => {
        const values = withSelected(facets[facet], filters[group]);
        if (values.length === 0) return null;
        return (
          <fieldset key={group}>
            <legend className="text-xs font-medium text-gray-700">{label}</legend>
            <ul className="mt-1 space-y-0.5">
              {values.map((v) => (
                <FacetRow
                  key={v.value}
                  label={v.value}
                  count={v.count}
                  checked={filters[group].includes(v.value)}
                  onChange={() => onToggle(group, v.value)}
                  testId={`facet-${group}-${v.value}`}
                />
              ))}
            </ul>
          </fieldset>
        );
      })}
      {facets.truncated && (
        <p className="text-xs text-amber-700">件数は一部のみです（対象が多いため上限で打ち切りました）。</p>
      )}
    </div>
  );
}
