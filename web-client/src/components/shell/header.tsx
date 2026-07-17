"use client";

import { Bars3Icon } from "@heroicons/react/24/outline";
import Link from "next/link";
import type { Workspace } from "@/lib/auth/workspaces";
import { ReplayTourButton } from "@/components/onboarding/replay-tour-button";
import { WorkspaceSwitcher } from "./workspace-switcher";
import { UserMenu } from "./user-menu";

type HeaderProps = {
  workspaces: Workspace[];
  currentWorkspace: Workspace | null;
  onSelectWorkspace: (workspace: Workspace) => void;
  displayName: string | null;
  onSignOut: () => void;
  /** Toggle the off-canvas sidebar drawer (mobile only). Omitted where there is no sidebar. */
  onToggleSidebar?: () => void;
  sidebarOpen?: boolean;
};

/** Global header: brand, workspace switcher (current hat), and the user menu. */
export function Header({
  workspaces,
  currentWorkspace,
  onSelectWorkspace,
  displayName,
  onSignOut,
  onToggleSidebar,
  sidebarOpen,
}: HeaderProps) {
  return (
    <header className="flex h-14 shrink-0 items-center gap-3 border-b border-gray-200 bg-white px-4 sm:gap-4">
      {onToggleSidebar && (
        <button
          type="button"
          onClick={onToggleSidebar}
          aria-label="メニュー"
          aria-expanded={sidebarOpen ?? false}
          aria-controls="app-sidebar"
          data-testid="sidebar-toggle"
          className="-ml-1 rounded p-1 text-gray-700 hover:bg-gray-100 md:hidden"
        >
          <Bars3Icon className="h-6 w-6" />
        </button>
      )}
      <Link href="/" className="text-base font-semibold text-gray-900">
        Building OS
      </Link>
      <WorkspaceSwitcher
        workspaces={workspaces}
        current={currentWorkspace}
        onSelect={onSelectWorkspace}
      />
      <div className="ml-auto flex items-center gap-4">
        <ReplayTourButton />
        <UserMenu displayName={displayName} onSignOut={onSignOut} />
      </div>
    </header>
  );
}
