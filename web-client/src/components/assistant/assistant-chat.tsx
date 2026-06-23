"use client";

import { usePathname } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import {
  buildAssistantContext,
  helpKeyForPath,
  isAssistantEnabled,
} from "@/lib/assistant/context";
import { postAssistantChat } from "@/lib/assistant/fetch-chat";
import type { ChatMessage } from "@/lib/assistant/types";
import { AssistantPanel } from "./assistant-panel";

/**
 * Experimental help-assistant launcher (#151). Rendered only when enabled
 * (`NEXT_PUBLIC_ASSISTANT_ENABLED=true`). Sends the current screen's D-1 context (#149) + the
 * conversation to `POST /api/assistant/chat`; read-only Q&A.
 */
export function AssistantChat() {
  const enabled = isAssistantEnabled();
  const pathname = usePathname();
  const [open, setOpen] = useState(false);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  if (!enabled) return null;

  const send = (text: string) => {
    const next: ChatMessage[] = [...messages, { role: "user", content: text }];
    setMessages(next);
    setBusy(true);
    setError(null);
    const helpKey = helpKeyForPath(pathname);
    const context = helpKey ? (buildAssistantContext(helpKey) ?? undefined) : undefined;
    postAssistantChat({ messages: next, context })
      .then((reply) => {
        if (mounted.current) {
          setMessages((m) => [...m, { role: "assistant", content: reply }]);
          setBusy(false);
        }
      })
      .catch((e: Error) => {
        if (mounted.current) {
          setError(e.message);
          setBusy(false);
        }
      });
  };

  return (
    <>
      {!open && (
        <button
          type="button"
          onClick={() => setOpen(true)}
          className="fixed bottom-4 right-4 z-40 rounded-full bg-blue-600 px-4 py-2 text-sm text-white shadow-lg hover:bg-blue-700"
        >
          ヘルプに質問
        </button>
      )}
      {open && (
        <AssistantPanel
          messages={messages}
          busy={busy}
          error={error}
          onSend={send}
          onClose={() => setOpen(false)}
        />
      )}
    </>
  );
}
