import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { ConfigEntry } from "@/lib/system-config/types";
import { EffectiveConfigView } from "./effective-config-view";

describe("EffectiveConfigView", () => {
  it("renders a row per entry and a secret badge, never the secret value", () => {
    const entries: ConfigEntry[] = [
      { key: "NATS_URL", isSecret: false, isSet: true, value: "nats://x:4222" },
      { key: "POSTGRES_CONNECTION_STRING", isSecret: true, isSet: true, value: null },
    ];
    render(<EffectiveConfigView entries={entries} />);
    expect(screen.getByTestId("config-row-NATS_URL")).toHaveTextContent("nats://x:4222");
    const secretRow = screen.getByTestId("config-row-POSTGRES_CONNECTION_STRING");
    expect(secretRow).toHaveTextContent("設定済み");
    expect(secretRow).toHaveTextContent("シークレット");
  });

  it("renders 未設定 for unset entries", () => {
    render(
      <EffectiveConfigView entries={[{ key: "PROMETHEUS_URL", isSecret: false, isSet: false }]} />,
    );
    expect(screen.getByTestId("config-row-PROMETHEUS_URL")).toHaveTextContent("未設定");
  });

  it("shows an empty state", () => {
    render(<EffectiveConfigView entries={[]} />);
    expect(screen.getByTestId("config-empty")).toBeInTheDocument();
  });
});
