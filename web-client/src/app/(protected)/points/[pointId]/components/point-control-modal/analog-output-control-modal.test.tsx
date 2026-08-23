import { aPointDetail } from "@/lib/resources/test-fixtures";
import type {
  ControlSchemaResource,
  PointDetailResource,
  PointResource,
} from "@/lib/resources/types";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AnalogOutputControlModal } from "./analog-output-control-modal";

// フィクスチャに手で範囲を与えると「API が範囲を返していない」事実をテストが隠してしまう (#298)。
// ここでは twin にある形 / 何も無い形の両方を、API がそのまま返す想定で組み立てる。
// Shared typed fixtures (src/lib/resources/test-fixtures.ts): the base used to be duplicated
// verbatim in each test file, so every new PointResource field meant three edits.
const detail = (
  point: Partial<PointResource>,
  controlSchema?: Partial<ControlSchemaResource>,
) => aPointDetail({ point, controlSchema });

const renderModal = (pointDetail: PointDetailResource) =>
  render(
    <AnalogOutputControlModal
      isOpen
      onClose={vi.fn()}
      pointDetail={pointDetail}
      onControl={vi.fn()}
      isLoading={false}
    />,
  );

const valueInput = () => screen.getByLabelText(/^値/) as HTMLInputElement;

describe("AnalogOutputControlModal range (#298)", () => {
  it("uses the ControlSchema range, the same bounds the server validates against", () => {
    renderModal(
      detail(
        { minPresValue: -10, maxPresValue: 50 },
        { dataType: "number", minValue: 16, maxValue: 30 },
      ),
    );

    expect(screen.getByText("値（16～30）")).toBeInTheDocument();
    expect(valueInput()).toHaveAttribute("min", "16");
    expect(valueInput()).toHaveAttribute("max", "30");
  });

  it("uses the twin's raw span when the point has no ControlSchema", () => {
    // 配線前は twin に -10〜50 があっても 0〜100 と表示し、その範囲で送信していた。
    renderModal(detail({ minPresValue: -10, maxPresValue: 50 }));

    expect(screen.getByText("値（-10～50）")).toBeInTheDocument();
    expect(valueInput()).toHaveAttribute("min", "-10");
    expect(valueInput()).toHaveAttribute("max", "50");
    expect(valueInput().value).toBe("-10");
  });

  it("does not invent a 0〜100 range when the twin has no bounds at all", () => {
    renderModal(detail({}));

    expect(screen.queryByText("値（0～100）")).not.toBeInTheDocument();
    expect(screen.getByTestId("range-unknown")).toBeInTheDocument();
    expect(valueInput()).not.toHaveAttribute("min");
    expect(valueInput()).not.toHaveAttribute("max");
  });
});
