"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { fetchGroups } from "@/lib/admin/fetch-groups";
import type { AdminGroup } from "@/lib/admin/types";
import { GroupsTable } from "./groups-table";

/** Loads the admin groups list and renders the pure {@link GroupsTable} (#143). */
export function GroupsPageClient() {
  const [groups, setGroups] = useState<AdminGroup[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    fetchGroups(controller.signal)
      .then((g) => {
        if (mounted.current) setGroups(g);
      })
      .catch((e: Error) => {
        if (e.name !== "AbortError" && mounted.current) setError(e.message);
      });
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, []);

  return (
    <div className="container mx-auto px-4 py-8">
      <div className="mb-4 flex items-center justify-between">
        <h1 className="text-2xl font-bold">グループ</h1>
        <Link
          href="/admin/groups/new"
          className="rounded bg-blue-600 px-3 py-2 text-sm text-white hover:bg-blue-700"
        >
          + 新規グループ
        </Link>
      </div>
      {error ? (
        <p className="text-red-600" data-testid="groups-error">
          グループの取得に失敗しました: {error}
        </p>
      ) : groups === null ? (
        <p className="text-gray-500">読み込み中…</p>
      ) : (
        <GroupsTable groups={groups} />
      )}
    </div>
  );
}
