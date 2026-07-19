import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OidcAuthProvider, useOidcAuth } from "./oidc-auth-provider";

// The provider calls useRouter(); no app-router is mounted in the unit test. Return a *stable*
// router object — a fresh one each call would change the useEffect([router]) dep every render.
vi.mock("next/navigation", () => {
  const router = { push: vi.fn(), replace: vi.fn() };
  return { useRouter: () => router };
});

function Probe() {
  const { isAuthenticated, claims, displayName } = useOidcAuth();
  return (
    <div data-testid="probe">
      {isAuthenticated ? `in:${claims.role}:${displayName}` : "out"}
    </div>
  );
}

describe("OidcAuthProvider — demo mode (#161)", () => {
  const original = process.env.NEXT_PUBLIC_DEMO_MODE;
  afterEach(() => {
    process.env.NEXT_PUBLIC_DEMO_MODE = original;
  });

  it("auto-logs-in as a demo admin without the Keycloak flow", async () => {
    process.env.NEXT_PUBLIC_DEMO_MODE = "true";
    render(
      <OidcAuthProvider>
        <Probe />
      </OidcAuthProvider>,
    );
    await waitFor(() =>
      expect(screen.getByTestId("probe")).toHaveTextContent(
        "in:admin:デモ管理者",
      ),
    );
  });
});
