import type { ResourceFacetsResult } from "@/lib/resources/types";
import { EMPTY_FILTERS } from "@/lib/resources/search-filters";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ResourceFacetPanel } from "./resource-facet-panel";

const facets: ResourceFacetsResult = {
  total: 14,
  truncated: false,
  types: [
    { value: "point", count: 12 },
    { value: "device", count: 2 },
  ],
  deviceTypes: [
    { value: "AHU", count: 9 },
    { value: "VAV", count: 3 },
  ],
  pointTypes: [{ value: "Temperature", count: 8 }],
  units: [{ value: "degC", count: 8 }],
  gateways: [{ value: "GW-1", count: 12 }],
};

function setup(over: Partial<React.ComponentProps<typeof ResourceFacetPanel>> = {}) {
  const onToggle = vi.fn();
  const onSelectType = vi.fn();
  render(
    <ResourceFacetPanel
      facets={facets}
      filters={EMPTY_FILTERS}
      onToggle={onToggle}
      onSelectType={onSelectType}
      {...over}
    />,
  );
  return { onToggle, onSelectType };
}

describe("ResourceFacetPanel (#454)", () => {
  it("lists every group's values with their counts", () => {
    setup();
    expect(screen.getByLabelText("AHU（9）")).toBeInTheDocument();
    expect(screen.getByLabelText("VAV（3）")).toBeInTheDocument();
    expect(screen.getByLabelText("Temperature（8）")).toBeInTheDocument();
    expect(screen.getByLabelText("degC（8）")).toBeInTheDocument();
    expect(screen.getByLabelText("GW-1（12）")).toBeInTheDocument();
    expect(screen.getByLabelText("ポイント（12）")).toBeInTheDocument();
  });

  it("reports a toggled attribute value with its group", () => {
    const { onToggle } = setup();
    fireEvent.click(screen.getByLabelText("AHU（9）"));
    expect(onToggle).toHaveBeenCalledWith("deviceTypes", "AHU");
    fireEvent.click(screen.getByLabelText("GW-1（12）"));
    expect(onToggle).toHaveBeenCalledWith("gatewayIds", "GW-1");
  });

  it("selecting a type calls onSelectType, and clicking the selected one clears it", () => {
    const { onSelectType } = setup();
    fireEvent.click(screen.getByLabelText("ポイント（12）"));
    expect(onSelectType).toHaveBeenCalledWith("point");
  });

  it("clicking the already-selected type clears it", () => {
    const { onSelectType } = setup({ filters: { ...EMPTY_FILTERS, type: "point" } });
    expect(screen.getByLabelText("ポイント（12）")).toBeChecked();
    fireEvent.click(screen.getByLabelText("ポイント（12）"));
    expect(onSelectType).toHaveBeenCalledWith("");
  });

  it("keeps a selected value that has no matches so it can still be unchecked", () => {
    setup({ filters: { ...EMPTY_FILTERS, units: ["ppm"] } });
    const box = screen.getByLabelText("ppm（0）");
    expect(box).toBeChecked();
  });

  it("omits groups that have no values and has no checkboxes while facets are not loaded", () => {
    setup({ facets: { ...facets, units: [], gateways: [] } });
    expect(screen.queryByText("単位")).not.toBeInTheDocument();
    expect(screen.queryByText("Gateway")).not.toBeInTheDocument();
  });

  it("says counts are a lower bound when the scan was cut", () => {
    setup({ facets: { ...facets, truncated: true } });
    expect(screen.getByText(/一部のみ/)).toBeInTheDocument();
  });

  it("renders nothing without facets", () => {
    const { container } = render(
      <ResourceFacetPanel facets={null} filters={EMPTY_FILTERS} onToggle={vi.fn()} onSelectType={vi.fn()} />,
    );
    expect(container).toBeEmptyDOMElement();
  });
});

describe("ResourceFacetPanel health groups (#454)", () => {
  const withHealth: ResourceFacetsResult = {
    ...facets,
    health: {
      freshness: [
        { value: "fresh", count: 7 },
        { value: "stale", count: 2 },
        { value: "missing", count: 1 },
      ],
      alarm: [
        { value: "warn", count: 3 },
        { value: "critical", count: 1 },
      ],
      dataComplete: true,
    },
  };
  const points = { ...EMPTY_FILTERS, type: "point" as const };

  it("offers Freshness and Alarm only while the subject is Points", () => {
    setup({ facets: withHealth, filters: points });
    expect(screen.getByLabelText("鮮度切れ（2）")).toBeInTheDocument();
    expect(screen.getByLabelText("欠測（1）")).toBeInTheDocument();
    expect(screen.getByLabelText("重大（1）")).toBeInTheDocument();
  });

  it("hides them for any other type", () => {
    setup({ facets: withHealth, filters: { ...EMPTY_FILTERS, type: "device" } });
    expect(screen.queryByTestId("facet-health-freshness")).not.toBeInTheDocument();
    setup({ facets: withHealth, filters: EMPTY_FILTERS });
    expect(screen.queryByTestId("facet-health-alarm")).not.toBeInTheDocument();
  });

  it("reports a toggled health value with its group", () => {
    const { onToggle } = setup({ facets: withHealth, filters: points });
    fireEvent.click(screen.getByLabelText("鮮度切れ（2）"));
    expect(onToggle).toHaveBeenCalledWith("freshness", "stale");
    fireEvent.click(screen.getByLabelText("警告（3）"));
    expect(onToggle).toHaveBeenCalledWith("alarm", "warn");
  });

  it("does not show counts while the last-seen index is still warming", () => {
    setup({
      facets: { ...withHealth, health: { ...withHealth.health!, dataComplete: false } },
      filters: points,
    });
    expect(screen.getByLabelText("欠測")).toBeInTheDocument(); // no "（1）": it would be a guess
    expect(screen.queryByLabelText("欠測（1）")).not.toBeInTheDocument();
    expect(screen.getByText(/走査中/)).toBeInTheDocument();
  });

  it("keeps a selected health value that currently matches nothing", () => {
    setup({
      facets: withHealth,
      filters: { ...points, alarm: ["critical"] },
    });
    expect(screen.getByLabelText("重大（1）")).toBeChecked();
  });

  it("shows no counts for an axis while the other health axis is selected (they cannot be ANDed)", () => {
    setup({ facets: withHealth, filters: { ...points, alarm: ["critical"] } });
    // alarm keeps its own counts (its own selection is excluded from them)
    expect(screen.getByLabelText("重大（1）")).toBeInTheDocument();
    // freshness counts ignore the alarm selection, so they would over-promise: hidden
    expect(screen.getByLabelText("鮮度切れ")).toBeInTheDocument();
    expect(screen.queryByLabelText("鮮度切れ（2）")).not.toBeInTheDocument();
  });

  it("shows the Point row without a count while a health condition is selected", () => {
    setup({
      facets: { ...withHealth, types: [{ value: "point", count: 0 }] },
      filters: { ...points, freshness: ["stale"] },
    });
    expect(screen.getByLabelText("ポイント")).toBeChecked();
    expect(screen.queryByLabelText("ポイント（0）")).not.toBeInTheDocument();
  });
});
