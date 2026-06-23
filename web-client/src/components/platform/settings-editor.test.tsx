import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { SettingView } from "@/lib/system-settings/types";
import { SettingsEditor } from "./settings-editor";

const boolSetting: SettingView = {
  key: "ui.showExperimentalFeatures",
  type: "Boolean",
  description: "flag",
  category: "ui",
  value: "false",
  defaultValue: "false",
  isOverridden: false,
  source: "Default",
};

const numSetting: SettingView = {
  key: "telemetry.staleThresholdSeconds",
  type: "Number",
  description: "threshold",
  category: "telemetry",
  value: "600",
  defaultValue: "300",
  isOverridden: true,
  source: "Ui",
};

describe("SettingsEditor", () => {
  it("toggles a boolean and saves the normalized value", () => {
    const onUpdate = vi.fn();
    render(<SettingsEditor settings={[boolSetting]} onUpdate={onUpdate} onReset={vi.fn()} />);
    fireEvent.click(screen.getByLabelText("ui.showExperimentalFeatures"));
    fireEvent.click(screen.getByRole("button", { name: "保存" }));
    expect(onUpdate).toHaveBeenCalledWith("ui.showExperimentalFeatures", "true");
  });

  it("blocks an invalid number and shows an error", () => {
    const onUpdate = vi.fn();
    render(<SettingsEditor settings={[numSetting]} onUpdate={onUpdate} onReset={vi.fn()} />);
    fireEvent.change(screen.getByLabelText("telemetry.staleThresholdSeconds"), {
      target: { value: "abc" },
    });
    fireEvent.click(screen.getByRole("button", { name: "保存" }));
    expect(onUpdate).not.toHaveBeenCalled();
    expect(screen.getByTestId("setting-error-telemetry.staleThresholdSeconds")).toBeInTheDocument();
  });

  it("shows reset for overridden settings and calls onReset", () => {
    const onReset = vi.fn();
    render(<SettingsEditor settings={[numSetting]} onUpdate={vi.fn()} onReset={onReset} />);
    expect(
      screen.getByTestId("setting-overridden-telemetry.staleThresholdSeconds"),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "既定値に戻す" }));
    expect(onReset).toHaveBeenCalledWith("telemetry.staleThresholdSeconds");
  });

  it("does not show reset for a default (non-overridden) setting", () => {
    render(<SettingsEditor settings={[boolSetting]} onUpdate={vi.fn()} onReset={vi.fn()} />);
    expect(screen.queryByRole("button", { name: "既定値に戻す" })).not.toBeInTheDocument();
  });

  it("shows an empty state", () => {
    render(<SettingsEditor settings={[]} onUpdate={vi.fn()} onReset={vi.fn()} />);
    expect(screen.getByTestId("settings-empty")).toBeInTheDocument();
  });
});
