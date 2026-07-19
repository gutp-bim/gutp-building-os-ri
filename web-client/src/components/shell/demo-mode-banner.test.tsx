import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { DemoModeBanner } from "./demo-mode-banner";

describe("DemoModeBanner (#161)", () => {
  const original = process.env.NEXT_PUBLIC_DEMO_MODE;
  afterEach(() => {
    process.env.NEXT_PUBLIC_DEMO_MODE = original;
  });

  it("shows the skipped-auth indicator in demo mode", () => {
    process.env.NEXT_PUBLIC_DEMO_MODE = "true";
    render(<DemoModeBanner />);
    expect(screen.getByTestId("demo-mode-banner")).toHaveTextContent(
      "認証フローをスキップ",
    );
  });

  it("renders nothing outside demo mode", () => {
    process.env.NEXT_PUBLIC_DEMO_MODE = "false";
    const { container } = render(<DemoModeBanner />);
    expect(container).toBeEmptyDOMElement();
  });
});
