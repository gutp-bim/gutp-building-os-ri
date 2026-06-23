"use client";

import * as DropdownMenu from "@radix-ui/react-dropdown-menu";
import { Check, ChevronsUpDown } from "lucide-react";
import { cn } from "@/lib/utils";
import { WORKSPACES, type Workspace, type WorkspaceMeta } from "@/lib/auth/workspaces";

type WorkspaceSwitcherProps = {
  /** Workspaces the current role may enter, in display order. */
  workspaces: Workspace[];
  /** The active workspace, or null before one is resolved. */
  current: Workspace | null;
  /** Called when the user picks a different workspace. */
  onSelect: (workspace: Workspace) => void;
};

/**
 * Header control that surfaces "which hat am I wearing". With a single workspace it is a static
 * label (no switching affordance); with several it becomes a dropdown. Switching is a UX
 * convenience — the API still enforces authorization on every request.
 */
export function WorkspaceSwitcher({ workspaces, current, onSelect }: WorkspaceSwitcherProps) {
  if (workspaces.length === 0) return null;

  const currentLabel = current ? WORKSPACES[current].label : WORKSPACES[workspaces[0]].label;

  if (workspaces.length === 1) {
    return (
      <span
        className="inline-flex items-center rounded-md bg-gray-100 px-3 py-1.5 text-sm font-medium text-gray-700"
        data-testid="workspace-label"
      >
        {currentLabel}
      </span>
    );
  }

  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger
        className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        aria-label="ワークスペースを切り替え"
      >
        {currentLabel}
        <ChevronsUpDown className="h-4 w-4 text-gray-400" aria-hidden />
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="start"
          sideOffset={4}
          className="z-50 min-w-[12rem] rounded-md border border-gray-200 bg-white p-1 shadow-md"
        >
          {workspaces.map((ws) => {
            const meta: WorkspaceMeta = WORKSPACES[ws];
            const isActive = ws === current;
            return (
              <DropdownMenu.Item
                key={ws}
                onSelect={() => onSelect(ws)}
                className={cn(
                  "flex cursor-pointer items-center justify-between rounded px-2 py-1.5 text-sm outline-none",
                  "data-[highlighted]:bg-gray-100",
                  isActive ? "font-medium text-gray-900" : "text-gray-700",
                )}
              >
                {meta.label}
                {isActive && <Check className="h-4 w-4 text-blue-600" aria-hidden />}
              </DropdownMenu.Item>
            );
          })}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
