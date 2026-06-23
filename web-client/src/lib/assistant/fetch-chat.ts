import Cookies from "js-cookie";
import type { AssistantChatRequest } from "./types";

const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:8081";

/**
 * `POST /api/assistant/chat` (#151). JWT-gated server-side (no backdoor). Returns the assistant reply.
 * A 503 means the assistant is disabled (Ollama optional profile not configured); 502 means the
 * upstream LLM failed.
 */
export async function postAssistantChat(
  request: AssistantChatRequest,
  signal?: AbortSignal,
): Promise<string> {
  const res = await fetch(`${API_BASE_URL}/api/assistant/chat`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${Cookies.get("oidc.access_token") || ""}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify(request),
    signal,
  });
  if (res.status === 503) {
    throw new Error("アシスタントは無効です（管理者が有効化していません）。");
  }
  if (!res.ok) {
    throw new Error(`アシスタントの応答に失敗しました (${res.status})`);
  }
  const data = (await res.json()) as { reply: string };
  return data.reply;
}
