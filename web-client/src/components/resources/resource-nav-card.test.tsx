import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

import { ResourceNavCard } from "./resource-nav-card";

describe("ResourceNavCard (#195)", () => {
  it("renders a keyboard-accessible link to the resource", () => {
    render(
      <ResourceNavCard href="/floors/f1" name="1F" id="floor:1" testId="floor-card" />,
    );
    const link = screen.getByTestId("floor-card");
    expect(link.tagName).toBe("A");
    expect(link).toHaveAttribute("href", "/floors/f1");
    // The accessible name is the concise resource name, not the long URN id read aloud.
    expect(link).toHaveAccessibleName("1F");
    expect(link).toHaveTextContent("ID: floor:1");
  });

  it("shows an optional subtitle", () => {
    render(
      <ResourceNavCard href="/devices/d1" name="AHU-1" subtitle="種別: 空調" id="dev:1" />,
    );
    expect(screen.getByText("種別: 空調")).toBeInTheDocument();
  });
});
