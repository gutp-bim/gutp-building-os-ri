"use client";

import { useEffect, useRef, useState } from "react";
import { fetchSettings, resetSetting, updateSetting } from "@/lib/system-settings/fetch-settings";
import type { SettingView } from "@/lib/system-settings/types";
import { HelpButton } from "@/components/help/help-button";
import { SettingsEditor } from "./settings-editor";

/**
 * Loads editable app settings and wires update/reset against `/api/system/settings` (#148). After each
 * mutation the list is refetched so effective values / override state stay accurate.
 */
export function SettingsClient() {
  const [settings, setSettings] = useState<SettingView[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [mutationError, setMutationError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    fetchSettings(controller.signal)
      .then((s) => {
        if (mounted.current) setSettings(s);
      })
      .catch((e: Error) => {
        if (e.name !== "AbortError" && mounted.current) setError(e.message);
      });
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, []);

  const runMutation = (op: () => Promise<unknown>) => {
    setBusy(true);
    setMutationError(null);
    op()
      .then(() => fetchSettings())
      .then((s) => {
        if (mounted.current) {
          setSettings(s);
          setBusy(false);
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
      <div className="mb-1 flex items-center gap-2">
        <h1 className="text-2xl font-bold">アプリ設定</h1>
        <HelpButton helpKey="platform.settings" />
      </div>
      <p className="mb-4 text-sm text-gray-500">
        フィーチャーフラグ/閾値など、GitOps と衝突しないアプリ設定のみを編集できます。インフラ/シークレットは
        読み取り専用の「設定（実効値）」で確認してください。
      </p>
      {mutationError && (
        <p className="mb-2 text-sm text-red-600" data-testid="settings-mutation-error">
          {mutationError}
        </p>
      )}
      {error ? (
        <p className="text-red-600" data-testid="settings-error">
          設定の取得に失敗しました: {error}
        </p>
      ) : settings === null ? (
        <p className="text-gray-500">読み込み中…</p>
      ) : (
        <SettingsEditor
          settings={settings}
          busy={busy}
          onUpdate={(key, value) => runMutation(() => updateSetting(key, value))}
          onReset={(key) => runMutation(() => resetSetting(key))}
        />
      )}
    </div>
  );
}
