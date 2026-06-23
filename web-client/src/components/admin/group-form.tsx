"use client";

import { useState } from "react";
import { validateGroupForm } from "@/lib/admin/group-form";
import type { GroupFormValues } from "@/lib/admin/types";

/**
 * Controlled group create/edit form (#143). Runs the pure {@link validateGroupForm} on submit and
 * only calls `onSubmit` when valid. In edit mode the id is immutable (read-only) and not required.
 */
export function GroupForm({
  mode,
  initial,
  submitting,
  submitError,
  onSubmit,
}: {
  mode: "create" | "edit";
  initial?: Partial<GroupFormValues>;
  submitting?: boolean;
  submitError?: string | null;
  onSubmit: (values: GroupFormValues) => void;
}) {
  const [values, setValues] = useState<GroupFormValues>({
    id: initial?.id ?? "",
    name: initial?.name ?? "",
    description: initial?.description ?? "",
  });
  const [validationError, setValidationError] = useState<string | null>(null);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    const result = validateGroupForm(values, { requireId: mode === "create" });
    if (!result.ok) {
      setValidationError(result.error);
      return;
    }
    setValidationError(null);
    onSubmit({
      ...values,
      id: values.id.trim(),
      name: values.name.trim(),
      description: values.description.trim(),
    });
  };

  const error = validationError ?? submitError ?? null;

  return (
    <form onSubmit={handleSubmit} className="max-w-xl space-y-4" data-testid="group-form">
      {error && (
        <p className="rounded bg-red-50 px-3 py-2 text-sm text-red-700" data-testid="group-form-error">
          {error}
        </p>
      )}
      <div>
        <label htmlFor="group-id" className="mb-1 block text-sm font-medium text-gray-700">
          ID{mode === "create" ? " *" : ""}
        </label>
        <input
          id="group-id"
          type="text"
          value={values.id}
          readOnly={mode === "edit"}
          onChange={(e) => setValues((v) => ({ ...v, id: e.target.value }))}
          className="w-full rounded border border-gray-300 px-3 py-2 read-only:bg-gray-100 focus:outline-none focus:ring-2 focus:ring-blue-500"
          placeholder="hvac-team"
        />
        {mode === "create" && (
          <p className="mt-1 text-xs text-gray-500">英数字とハイフンのみ使用できます</p>
        )}
      </div>
      <div>
        <label htmlFor="group-name" className="mb-1 block text-sm font-medium text-gray-700">
          名前 *
        </label>
        <input
          id="group-name"
          type="text"
          value={values.name}
          onChange={(e) => setValues((v) => ({ ...v, name: e.target.value }))}
          className="w-full rounded border border-gray-300 px-3 py-2 focus:outline-none focus:ring-2 focus:ring-blue-500"
          placeholder="HVAC管理チーム"
        />
      </div>
      <div>
        <label htmlFor="group-description" className="mb-1 block text-sm font-medium text-gray-700">
          説明
        </label>
        <textarea
          id="group-description"
          value={values.description}
          rows={3}
          onChange={(e) => setValues((v) => ({ ...v, description: e.target.value }))}
          className="w-full rounded border border-gray-300 px-3 py-2 focus:outline-none focus:ring-2 focus:ring-blue-500"
          placeholder="空調設備の管理担当グループ"
        />
      </div>
      <button
        type="submit"
        disabled={submitting}
        className="rounded bg-blue-600 px-4 py-2 text-white hover:bg-blue-700 disabled:opacity-50"
      >
        {submitting ? "保存中…" : mode === "create" ? "作成" : "更新"}
      </button>
    </form>
  );
}
