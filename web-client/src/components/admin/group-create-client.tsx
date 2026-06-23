"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { createGroup } from "@/lib/admin/fetch-groups";
import type { GroupFormValues } from "@/lib/admin/types";
import { GroupForm } from "./group-form";

/** New-group page client (#143): submits {@link createGroup} then navigates to the detail page. */
export function GroupCreateClient() {
  const router = useRouter();
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const handleSubmit = (values: GroupFormValues) => {
    setSubmitting(true);
    setError(null);
    createGroup(values)
      .then((group) => {
        // Navigate away on success; no setState needed (the route unmounts this component).
        router.push(group.id ? `/admin/groups/${encodeURIComponent(group.id)}` : "/admin/groups");
      })
      .catch((e: Error) => {
        if (mounted.current) {
          setError(e.message);
          setSubmitting(false);
        }
      });
  };

  return (
    <div className="container mx-auto px-4 py-8">
      <Link href="/admin/groups" className="mb-4 inline-block text-sm text-blue-600 hover:underline">
        ← グループ一覧へ
      </Link>
      <h1 className="mb-4 text-2xl font-bold">グループ作成</h1>
      <GroupForm mode="create" submitting={submitting} submitError={error} onSubmit={handleSubmit} />
    </div>
  );
}
