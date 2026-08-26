import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

// jsdom implements no ResizeObserver, and recharts' ResponsiveContainer constructs one on mount — so
// any test that renders a chart throws without this. The stub reports nothing, which is fine: the
// container measures to 0 in jsdom regardless, so chart tests assert on the surrounding markup
// (empty-state branch, controls) rather than SVG geometry.
if (!("ResizeObserver" in globalThis)) {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

// Ensure the DOM is reset between tests so component trees don't leak across cases.
afterEach(() => {
  cleanup();
});
