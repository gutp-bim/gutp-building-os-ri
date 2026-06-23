"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import {
  addGroupResource,
  deleteGroup,
  fetchGroup,
  removeGroupResource,
  updateGroup,
} from "@/lib/admin/fetch-groups";
import type { EditResourceType } from "@/lib/admin/resource-edit";
import type { AdminGroupDetail, GroupFormValues } from "@/lib/admin/types";
import { GroupDetailView } from "./group-detail-view";
import { GroupForm } from "./group-form";
import { GroupResourceManager } from "./group-resource-manager";

/**
 * Loads one admin group and renders attributes ({@link GroupDetailView}), edit (PUT) / delete (DELETE)
 * controls, and an editable resource list ({@link GroupResourceManager}) wired to the `…/resources*`
 * endpoints (#143). The recursive tree-browse picker over the digital twin is a shared follow-up.
 */
export function GroupDetailClient({ id }: { id: string }) {
  const router = useRouter();
  const [group, setGroup] = useState<AdminGroupDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [mutationError, setMutationError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    fetchGroup(id, controller.signal)
      .then((g) => {
        if (mounted.current) setGroup(g);
      })
      .catch((e: Error) => {
        if (e.name !== "AbortError" && mounted.current) setError(e.message);
      });
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, [id]);

  const handleUpdate = (values: GroupFormValues) => {
    setSubmitting(true);
    setMutationError(null);
    updateGroup(id, values)
      .then(() => fetchGroup(id))
      .then((g) => {
        if (mounted.current) {
          setGroup(g);
          setEditing(false);
          setSubmitting(false);
        }
      })
      .catch((e: Error) => {
        if (mounted.current) {
          setMutationError(e.message);
          setSubmitting(false);
        }
      });
  };

  const handleDelete = () => {
    if (!group) return;
    if (!window.confirm(`グループ「${group.name ?? id}」を削除しますか？`)) return;
    setSubmitting(true);
    setMutationError(null);
    deleteGroup(id)
      .then(() => router.push("/admin/groups"))
      .catch((e: Error) => {
        if (mounted.current) {
          setMutationError(e.message);
          setSubmitting(false);
        }
      });
  };

  /** Runs a resource mutation then refetches the group so the member list reflects the change. */
  const runResourceMutation = (op: () => Promise<unknown>) => {
    setSubmitting(true);
    setMutationError(null);
    op()
      .then(() => fetchGroup(id))
      .then((g) => {
        if (mounted.current) {
          setGroup(g);
          setSubmitting(false);
        }
      })
      .catch((e: Error) => {
        if (mounted.current) {
          setMutationError(e.message);
          setSubmitting(false);
        }
      });
  };

  const handleAddResource = (resourceType: EditResourceType, resourceId: string) =>
    runResourceMutation(() => addGroupResource(id, resourceType, resourceId));

  const handleRemoveResource = (itemId: string) =>
    runResourceMutation(() => removeGroupResource(id, itemId));

  return (
    <div className="container mx-auto px-4 py-8">
      <Link href="/admin/groups" className="mb-4 inline-block text-sm text-blue-600 hover:underline">
        ← グループ一覧へ
      </Link>
      <div className="mb-4 flex items-center justify-between">
        <h1 className="text-2xl font-bold">グループ詳細</h1>
        {group && !editing && (
          <div className="space-x-2">
            <button
              type="button"
              onClick={() => setEditing(true)}
              className="rounded border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50"
            >
              編集
            </button>
            <button
              type="button"
              onClick={handleDelete}
              disabled={submitting}
              className="rounded border border-red-300 px-3 py-1.5 text-sm text-red-600 hover:bg-red-50 disabled:opacity-50"
            >
              削除
            </button>
          </div>
        )}
      </div>

      {error ? (
        <p className="text-red-600" data-testid="group-error">
          グループの取得に失敗しました: {error}
        </p>
      ) : group === null ? (
        <p className="text-gray-500">読み込み中…</p>
      ) : editing ? (
        <>
          <GroupForm
            mode="edit"
            initial={{ id: group.id ?? id, name: group.name ?? "", description: group.description ?? "" }}
            submitting={submitting}
            submitError={mutationError}
            onSubmit={handleUpdate}
          />
          <button
            type="button"
            onClick={() => {
              setEditing(false);
              setMutationError(null);
            }}
            className="mt-2 text-sm text-gray-500 hover:underline"
          >
            キャンセル
          </button>
        </>
      ) : (
        <>
          {mutationError && (
            <p className="mb-2 text-sm text-red-600" data-testid="group-mutation-error">
              {mutationError}
            </p>
          )}
          <GroupDetailView group={group} />
          <h2 className="mb-2 mt-6 text-lg font-semibold">
            リソース（{group.resourceItems?.length ?? 0}）
          </h2>
          <GroupResourceManager
            items={group.resourceItems ?? []}
            busy={submitting}
            onAdd={handleAddResource}
            onRemove={handleRemoveResource}
          />
        </>
      )}
    </div>
  );
}
