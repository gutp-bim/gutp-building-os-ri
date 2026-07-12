"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { fetchRoles, fetchUsers } from "@/lib/admin/fetch-users";
import type { AdminUser, RoleCatalogEntry } from "@/lib/admin/types";
import {
  EMPTY_USER_FILTER,
  filterUsers,
  roleLabel,
  type RoleFilter,
  type UserStatusFilter,
} from "@/lib/admin/users-display";
import { RoleCatalogPanel } from "./role-catalog-panel";
import { UsersTable } from "./users-table";

/**
 * Loads the admin users list + role catalog and renders the aggregated view with role/status/text
 * filters (#325). Role + permission assignment lives on the detail page; account creation is in
 * Keycloak (surfaced as a notice here).
 */
export function UsersPageClient() {
  const [users, setUsers] = useState<AdminUser[] | null>(null);
  const [roles, setRoles] = useState<RoleCatalogEntry[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [role, setRole] = useState<RoleFilter>("all");
  const [status, setStatus] = useState<UserStatusFilter>("all");
  const [query, setQuery] = useState("");
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    fetchUsers(controller.signal)
      .then((u) => {
        if (mounted.current) setUsers(u);
      })
      .catch((e: Error) => {
        if (e.name !== "AbortError" && mounted.current) setError(e.message);
      });
    // Role catalog is best-effort; failure should not block the list.
    fetchRoles(controller.signal)
      .then((r) => {
        if (mounted.current) setRoles(r);
      })
      .catch(() => {});
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, []);

  const filtered = useMemo(
    () => (users ? filterUsers(users, { ...EMPTY_USER_FILTER, role, status, query }) : []),
    [users, role, status, query],
  );

  return (
    <div className="container mx-auto px-4 py-8">
      <h1 className="mb-1 text-2xl font-bold">ユーザー＆ロール</h1>
      <p className="mb-4 text-sm text-gray-600" data-testid="keycloak-notice">
        新規ユーザーの作成・パスワード・MFA は Keycloak で行います。ここではロール割当・権限・有効/無効を管理します。
      </p>

      {error ? (
        <p className="text-red-600" data-testid="users-error">
          ユーザーの取得に失敗しました: {error}
        </p>
      ) : users === null ? (
        <p className="text-gray-600">読み込み中…</p>
      ) : (
        <div className="space-y-4">
          <div className="flex flex-wrap items-center gap-3" data-testid="user-filters">
            <input
              type="search"
              placeholder="名前・メールで検索"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              className="rounded border border-gray-300 px-2 py-1 text-sm"
              data-testid="filter-query"
            />
            <select
              value={role}
              onChange={(e) => setRole(e.target.value)}
              className="rounded border border-gray-300 px-2 py-1 text-sm"
              data-testid="filter-role"
            >
              <option value="all">すべてのロール</option>
              {(roles.length > 0 ? roles.map((r) => r.role) : ["admin", "operator", "viewer"]).map((r) => (
                <option key={r} value={r}>
                  {roleLabel(r)}
                </option>
              ))}
            </select>
            <select
              value={status}
              onChange={(e) => setStatus(e.target.value as UserStatusFilter)}
              className="rounded border border-gray-300 px-2 py-1 text-sm"
              data-testid="filter-status"
            >
              <option value="all">すべての状態</option>
              <option value="enabled">有効のみ</option>
              <option value="disabled">無効のみ</option>
            </select>
            <span className="text-xs text-gray-600" data-testid="filter-count">
              {filtered.length} / {users.length} 件
            </span>
          </div>

          <UsersTable users={filtered} />
          <RoleCatalogPanel roles={roles} />
        </div>
      )}
    </div>
  );
}
