import { render } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const push = vi.fn();
const useAuthMock = vi.fn();

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push }),
}));
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => useAuthMock(),
}));

import SignInPage from "./page";

describe("SignInPage post-login redirect (#191)", () => {
  beforeEach(() => {
    push.mockClear();
  });

  it("sends an already-authenticated visitor to /home, not /buildings", () => {
    useAuthMock.mockReturnValue({
      signInWithOidc: vi.fn(),
      isAuthenticated: true,
    });
    render(<SignInPage />);
    expect(push).toHaveBeenCalledWith("/home");
    expect(push).not.toHaveBeenCalledWith("/buildings");
  });

  it("does not redirect an unauthenticated visitor", () => {
    useAuthMock.mockReturnValue({
      signInWithOidc: vi.fn(),
      isAuthenticated: false,
    });
    render(<SignInPage />);
    expect(push).not.toHaveBeenCalled();
  });
});
