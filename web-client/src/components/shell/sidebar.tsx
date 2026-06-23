"use client";

import Link from "next/link";
import { cn } from "@/lib/utils";
import type { Workspace } from "@/lib/auth/workspaces";
import { isNavItemActive } from "@/lib/nav/active";
import { visibleNavItems } from "@/lib/nav/nav-config";

type SidebarProps = {
  workspace: Workspace | null;
  permissions: string[];
  pathname: string;
};

/** Workspace-scoped navigation. Items the user lacks permission for are simply not rendered. */
export function Sidebar({ workspace, permissions, pathname }: SidebarProps) {
  const items = workspace ? visibleNavItems(workspace, permissions) : [];

  return (
    <nav
      aria-label="メインナビゲーション"
      className="flex w-56 shrink-0 flex-col gap-1 border-r border-gray-200 bg-gray-50 p-3"
    >
      {items.map((item) => {
        const active = isNavItemActive(pathname, item);
        return (
          <Link
            key={item.href}
            href={item.href}
            aria-current={active ? "page" : undefined}
            className={cn(
              "rounded-md px-3 py-2 text-sm font-medium transition-colors",
              active
                ? "bg-blue-100 text-blue-800"
                : "text-gray-700 hover:bg-gray-100 hover:text-gray-900",
            )}
          >
            {item.label}
          </Link>
        );
      })}
    </nav>
  );
}
