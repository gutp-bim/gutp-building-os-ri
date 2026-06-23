"use client";

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
};

/** Global header: brand, workspace switcher (current hat), and the user menu. */
export function Header({
  workspaces,
  currentWorkspace,
  onSelectWorkspace,
  displayName,
  onSignOut,
}: HeaderProps) {
  return (
    <header className="flex h-14 shrink-0 items-center gap-4 border-b border-gray-200 bg-white px-4">
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
