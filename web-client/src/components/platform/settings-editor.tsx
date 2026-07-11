"use client";

import { useState } from "react";
import { validateSettingInput } from "@/lib/system-settings/validate";
import type { SettingView } from "@/lib/system-settings/types";

/**
 * Editable app-settings table (#148). Each row edits one allowlisted setting with a type-appropriate
 * control; updates are client-validated then sent via `onUpdate`. Overridden rows can reset to default.
 * Presentational — the parent runs the mutations.
 */
export function SettingsEditor({
  settings,
  busy,
  onUpdate,
  onReset,
}: {
  settings: SettingView[];
  busy?: boolean;
  onUpdate: (key: string, value: string) => void;
  onReset: (key: string) => void;
}) {
  if (settings.length === 0) {
    return (
      <p className="text-gray-500" data-testid="settings-empty">
        編集可能な設定がありません
      </p>
    );
  }
  return (
    <ul className="flex flex-col gap-4" data-testid="settings-list">
      {settings.map((s) => (
        <SettingRow key={s.key} setting={s} busy={busy} onUpdate={onUpdate} onReset={onReset} />
      ))}
    </ul>
  );
}

function SettingRow({
  setting,
  busy,
  onUpdate,
  onReset,
}: {
  setting: SettingView;
  busy?: boolean;
  onUpdate: (key: string, value: string) => void;
  onReset: (key: string) => void;
}) {
  const [draft, setDraft] = useState(setting.value);
  const [error, setError] = useState<string | null>(null);

  const submit = () => {
    const result = validateSettingInput(setting.type, draft);
    if (!result.ok) {
      setError(result.error);
      return;
    }
    setError(null);
    onUpdate(setting.key, result.normalized);
  };

  return (
    <li className="rounded border border-gray-200 p-3" data-testid={`setting-row-${setting.key}`}>
      <div className="flex items-baseline justify-between gap-2">
        <span className="font-mono text-sm">{setting.key}</span>
        <span className="text-xs text-gray-500">{setting.category}</span>
      </div>
      <p className="mb-2 text-xs text-gray-500">{setting.description}</p>

      <div className="flex flex-wrap items-center gap-2">
        {setting.type === "Boolean" ? (
          <label className="flex items-center gap-1 text-sm">
            <input
              type="checkbox"
              aria-label={setting.key}
              checked={draft.toLowerCase() === "true"}
              onChange={(e) => setDraft(e.target.checked ? "true" : "false")}
            />
            {draft.toLowerCase() === "true" ? "有効" : "無効"}
          </label>
        ) : (
          <input
            aria-label={setting.key}
            type={setting.type === "Number" ? "number" : "text"}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            className="rounded border border-gray-300 px-2 py-1 text-sm"
          />
        )}
        <button
          type="button"
          onClick={submit}
          disabled={busy}
          className="rounded bg-blue-600 px-3 py-1 text-sm text-white hover:bg-blue-700 disabled:opacity-50"
        >
          保存
        </button>
        {setting.isOverridden ? (
          <>
            <button
              type="button"
              onClick={() => onReset(setting.key)}
              disabled={busy}
              className="rounded border border-gray-300 px-3 py-1 text-sm hover:bg-gray-50 disabled:opacity-50"
            >
              既定値に戻す
            </button>
            <span className="text-xs text-amber-700" data-testid={`setting-overridden-${setting.key}`}>
              上書き中（既定: {setting.defaultValue}）
            </span>
          </>
        ) : (
          <span className="text-xs text-gray-500">既定値</span>
        )}
      </div>
      {error && (
        <p className="mt-1 text-sm text-red-600" data-testid={`setting-error-${setting.key}`}>
          {error}
        </p>
      )}
    </li>
  );
}
