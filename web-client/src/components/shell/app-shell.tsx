"use client";

import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useOidcAuth } from "@/lib/auth/oidc-auth-provider";
import {
  WORKSPACES,
  type Workspace,
  defaultWorkspace,
  workspacesForRole,
} from "@/lib/auth/workspaces";
import { workspaceForPath } from "@/lib/nav/active";
import { OnboardingTour } from "@/components/onboarding/onboarding-tour";
import { AssistantChat } from "@/components/assistant/assistant-chat";
import { Breadcrumb } from "./breadcrumb";
import { Header } from "./header";
import { Sidebar } from "./sidebar";

/**
 * The persistent application frame (header + sidebar) wrapped around every protected page. It reads
 * the signed-in user's role/permissions from the OIDC context and derives the current workspace
 * from the URL, so the shell stays in sync with deep links and the browser back button.
 *
 * On narrow viewports the sidebar collapses into an off-canvas drawer toggled from the header
 * hamburger (#199); on `md`+ it is a static column as before.
 */
export function AppShell({ children }: { children: React.ReactNode }) {
  const { claims, displayName, signOut } = useOidcAuth();
  const pathname = usePathname();
  const router = useRouter();
  const [sidebarOpen, setSidebarOpen] = useState(false);

  const { role, permissions } = claims;
  const workspaces = workspacesForRole(role);
  const currentWorkspace = workspaceForPath(pathname) ?? defaultWorkspace(role);

  // Close the mobile drawer whenever the route changes (a nav link was followed).
  useEffect(() => setSidebarOpen(false), [pathname]);

  const onSelectWorkspace = (ws: Workspace) => {
    router.push(WORKSPACES[ws].defaultPath);
  };

  return (
    <div className="flex h-screen flex-col">
      <Header
        workspaces={workspaces}
        currentWorkspace={currentWorkspace}
        onSelectWorkspace={onSelectWorkspace}
        displayName={displayName}
        onSignOut={signOut}
        onToggleSidebar={() => setSidebarOpen((open) => !open)}
        sidebarOpen={sidebarOpen}
      />
      <div className="flex min-h-0 flex-1">
        <Sidebar
          workspace={currentWorkspace}
          permissions={permissions}
          pathname={pathname}
          open={sidebarOpen}
          onClose={() => setSidebarOpen(false)}
        />
        <main className="min-w-0 flex-1 overflow-auto">
          <Breadcrumb pathname={pathname} />
          {children}
        </main>
      </div>
      <OnboardingTour role={role} />
      <AssistantChat />
    </div>
  );
}
