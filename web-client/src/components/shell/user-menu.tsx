"use client";

import * as DropdownMenu from "@radix-ui/react-dropdown-menu";
import { LogOut, User as UserIcon } from "lucide-react";

type UserMenuProps = {
  displayName: string | null;
  onSignOut: () => void;
};

/** Header control showing who is signed in and the way out. */
export function UserMenu({ displayName, onSignOut }: UserMenuProps) {
  const name = displayName ?? "ユーザー";
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger
        className="inline-flex items-center gap-2 rounded-md px-2 py-1.5 text-sm text-gray-700 hover:bg-gray-100 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
        aria-label="ユーザーメニュー"
      >
        <span className="flex h-7 w-7 items-center justify-center rounded-full bg-gray-200 text-gray-600">
          <UserIcon className="h-4 w-4" aria-hidden />
        </span>
        <span className="max-w-[10rem] truncate">{name}</span>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          sideOffset={4}
          className="z-50 min-w-[10rem] rounded-md border border-gray-200 bg-white p-1 shadow-md"
        >
          <DropdownMenu.Item
            onSelect={onSignOut}
            className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm text-gray-700 outline-none data-[highlighted]:bg-gray-100"
          >
            <LogOut className="h-4 w-4" aria-hidden />
            サインアウト
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}
