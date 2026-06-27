import type { ResourceMetadata, ResourceRef } from "@/lib/resources/types";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ResourceDetail } from "./resource-detail";

const building: ResourceRef = {
  type: "building",
  dtId: "https://www.sbco.or.jp/ont/resource/building%3Asite%3Asite-1%2Fbldg-1",
  id: "building:site:site-1/bldg-1",
  name: "bldg-1",
};

describe("ResourceDetail", () => {
  it("shows the SBCO ontology class for the selected resource", () => {
    render(<ResourceDetail resource={building} />);
    const cls = screen.getByTestId("sbco-class");
    expect(cls).toHaveTextContent("sbco:Building");
  });

  it("renders SBCO class for a point as sbco:PointExt", () => {
    render(
      <ResourceDetail
        resource={{ type: "point", dtId: "urn:pt1", id: "PT001", name: "Room Temp" }}
      />,
    );
    expect(screen.getByTestId("sbco-class")).toHaveTextContent("sbco:PointExt");
  });

  it("shows a percent-decoded dtId while keeping the raw value in the title (backward compatible)", () => {
    render(<ResourceDetail resource={building} />);
    const dtid = screen.getByTestId("dtid");
    expect(dtid).toHaveTextContent(
      "https://www.sbco.or.jp/ont/resource/building:site:site-1/bldg-1",
    );
    // raw (encoded) value preserved for copy/debug
    expect(dtid).toHaveAttribute("title", building.dtId);
  });

  it("renders empty state when no resource is selected", () => {
    render(<ResourceDetail resource={null} />);
    expect(screen.getByTestId("detail-empty")).toBeInTheDocument();
  });

  // ── Metadata section ──────────────────────────────────────────────────────

  const metadata: ResourceMetadata = {
    identifiers: { ifcGuid: "3Skg8nAD1AJAiNfIxGkWjF" },
    customTags: { geometryMapped: true },
  };

  it("shows identifiers when metadata is provided", () => {
    render(<ResourceDetail resource={building} metadata={metadata} />);
    expect(screen.getByTestId("metadata-section")).toBeInTheDocument();
    expect(screen.getByText("ifcGuid")).toBeInTheDocument();
    expect(screen.getByText("3Skg8nAD1AJAiNfIxGkWjF")).toBeInTheDocument();
  });

  it("shows customTags when metadata is provided", () => {
    render(<ResourceDetail resource={building} metadata={metadata} />);
    expect(screen.getByText("geometryMapped")).toBeInTheDocument();
  });

  it("shows edit button when canWrite is true", () => {
    render(<ResourceDetail resource={building} metadata={metadata} canWrite />);
    expect(screen.getByTestId("metadata-edit-btn")).toBeInTheDocument();
  });

  it("hides edit button when canWrite is false", () => {
    render(<ResourceDetail resource={building} metadata={metadata} canWrite={false} />);
    expect(screen.queryByTestId("metadata-edit-btn")).not.toBeInTheDocument();
  });

  it("does not show metadata section when metadata is not provided", () => {
    render(<ResourceDetail resource={building} />);
    expect(screen.queryByTestId("metadata-section")).not.toBeInTheDocument();
  });
});
