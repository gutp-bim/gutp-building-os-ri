/**
 * Help-assistant types (#151). Read-only Q&A: the client sends the current screen's D-1 help context
 * (resolved client-side) + the conversation; the server injects guardrails and proxies to a local LLM.
 */
export type ChatRole = "user" | "assistant";

export interface ChatMessage {
  role: ChatRole;
  content: string;
}

export interface AssistantContextTerm {
  term: string;
  definition: string;
}

export interface AssistantHelpContext {
  title?: string;
  body: string[];
  terms: AssistantContextTerm[];
}

export interface AssistantChatRequest {
  messages: ChatMessage[];
  context?: AssistantHelpContext;
}
