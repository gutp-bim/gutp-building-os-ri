"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import {
  addUserPermission,
  fetchUser,
  removeUserPermission,
  setUserEnabled,
} from "@/lib/admin/fetch-users";
import { resolvePermissionIds } from "@/lib/admin/fetch-permissions";
import { collectResolvableIds, type ResolvedMap } from "@/lib/admin/permission-resolve";
import type { AdminUser } from "@/lib/admin/types";
import { isEnabled } from "@/lib/admin/users-display";
import { PermissionEditor } from "./permission-editor";
import { UserDetailView } from "./user-detail-view";

/**
 * Loads one admin user and renders attributes ({@link UserDetailView}) plus an editable permission
 * list ({@link PermissionEditor}) wired to `POST`/`DELETE /api/Users/{id}/permissions` (#143).
 */
export function UserDetailClient({ id }: { id: string }) {
  const [user, setUser] = useState<AdminUser | null>(null);
  const [resolved, setResolved] = useState<ResolvedMap>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [mutationError, setMutationError] = useState<string | null>(null);
  const mounted = useRef(true);

  /** Best-effort hashed-id → name resolution; failures leave the raw ids on screen (#143). */
  const resolvePerms = (permissions: string[], signal?: AbortSignal) => {
    const ids = collectResolvableIds(permissions);
    if (ids.length === 0) return;
    resolvePermissionIds(ids, signal)
      .then((map) => {
        if (mounted.current) setResolved((prev) => ({ ...prev, ...map }));
      })
      .catch(() => {
        /* best-effort — keep showing raw ids */
      });
  };

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    fetchUser(id, controller.signal)
      .then((u) => {
        if (mounted.current) {
          setUser(u);
          resolvePerms(u.permissions ?? [], controller.signal);
        }
      })
      .catch((e: Error) => {
        if (e.name !== "AbortError" && mounted.current) setError(e.message);
      });
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, [id]);

  const runMutation = (op: () => Promise<AdminUser>) => {
    setBusy(true);
    setMutationError(null);
    op()
      .then((updated) => {
        if (mounted.current) {
          setUser(updated);
          setBusy(false);
          resolvePerms(updated.permissions ?? []);
        }
      })
      .catch((e: Error) => {
        if (mounted.current) {
          setMutationError(e.message);
          setBusy(false);
        }
      });
  };

  return (
    <div className="container mx-auto px-4 py-8">
      <Link href="/admin/users" className="mb-4 inline-block text-sm text-blue-600 hover:underline">
        ← ユーザー一覧へ
      </Link>
      <h1 className="mb-4 text-2xl font-bold">ユーザー詳細</h1>
      {error ? (
        <p className="text-red-600" data-testid="user-error">
          ユーザーの取得に失敗しました: {error}
        </p>
      ) : user === null ? (
        <p className="text-gray-500">読み込み中…</p>
      ) : (
        <>
          <UserDetailView user={user} />

          <div className="mt-4 flex items-center gap-3" data-testid="enabled-control">
            <span className="text-sm text-gray-600">
              状態: <strong>{isEnabled(user) ? "有効" : "無効"}</strong>
            </span>
            <button
              type="button"
              disabled={busy}
              onClick={() => runMutation(() => setUserEnabled(id, !isEnabled(user)))}
              className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
              data-testid="toggle-enabled"
            >
              {isEnabled(user) ? "無効化する" : "有効化する"}
            </button>
          </div>

          <h2 className="mb-2 mt-6 text-lg font-semibold">権限</h2>
          {mutationError && (
            <p className="mb-2 text-sm text-red-600" data-testid="permission-mutation-error">
              {mutationError}
            </p>
          )}
          <PermissionEditor
            permissions={user.permissions ?? []}
            busy={busy}
            resolved={resolved}
            onAdd={(perm) => runMutation(() => addUserPermission(id, perm))}
            onRemove={(perm) => runMutation(() => removeUserPermission(id, perm))}
          />
        </>
      )}
    </div>
  );
}
