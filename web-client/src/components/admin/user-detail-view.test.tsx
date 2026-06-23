import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { UserDetailView } from "./user-detail-view";

describe("UserDetailView", () => {
  it("renders the user attributes", () => {
    render(
      <UserDetailView
        user={{ id: "u1", displayName: "Alice", email: "a@x.jp", role: "admin" }}
      />,
    );
    const detail = screen.getByTestId("user-detail");
    expect(detail).toHaveTextContent("Alice");
    expect(detail).toHaveTextContent("a@x.jp");
    expect(detail).toHaveTextContent("admin");
  });

  it("falls back to a dash for missing attributes", () => {
    render(<UserDetailView user={{ id: "u2", displayName: "Bob" }} />);
    expect(screen.getByTestId("user-detail")).toHaveTextContent("—");
  });
});
