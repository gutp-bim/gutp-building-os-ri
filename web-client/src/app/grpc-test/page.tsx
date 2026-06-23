"use client";

import { Greeter, type NotificationEvent } from "@/lib/gen/greet_pb";
import { createClient } from "@connectrpc/connect";
import { createGrpcWebTransport } from "@connectrpc/connect-web";
import { useCallback, useRef, useState } from "react";

const transport = createGrpcWebTransport({
  baseUrl: process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:8080",
});

const client = createClient(Greeter, transport);

export default function GrpcTestPage() {
  // --- SayHello (Unary) ---
  const [name, setName] = useState("");
  const [helloResponse, setHelloResponse] = useState<string | null>(null);
  const [helloLoading, setHelloLoading] = useState(false);

  // --- Subscribe (Server Streaming) ---
  const [clientName, setClientName] = useState("");
  const [events, setEvents] = useState<NotificationEvent[]>([]);
  const [subscribed, setSubscribed] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const [streamError, setStreamError] = useState<string | null>(null);

  const [error, setError] = useState<string | null>(null);

  const handleSayHello = async (e: React.FormEvent) => {
    e.preventDefault();
    setHelloLoading(true);
    setHelloResponse(null);
    setError(null);
    try {
      const reply = await client.sayHello({ name });
      setHelloResponse(reply.message);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setHelloLoading(false);
    }
  };

  const handleSubscribe = useCallback(async () => {
    if (subscribed) return;
    setEvents([]);
    setStreamError(null);
    setSubscribed(true);

    const abort = new AbortController();
    abortRef.current = abort;

    try {
      for await (const event of client.subscribe(
        { clientName },
        { signal: abort.signal },
      )) {
        setEvents((prev) => [...prev, event]);
      }
    } catch (err) {
      if (abort.signal.aborted) return;
      setStreamError(err instanceof Error ? err.message : String(err));
    } finally {
      setSubscribed(false);
    }
  }, [clientName, subscribed]);

  const handleUnsubscribe = useCallback(() => {
    abortRef.current?.abort();
    abortRef.current = null;
  }, []);

  return (
    <div style={{ maxWidth: 600, margin: "40px auto", fontFamily: "sans-serif" }}>
      <h1 style={{ fontSize: 24, marginBottom: 24 }}>gRPC-Web Test</h1>

      {/* --- Unary RPC --- */}
      <section style={{ marginBottom: 32 }}>
        <h2 style={{ fontSize: 18, marginBottom: 12 }}>Unary: SayHello</h2>
        <form onSubmit={handleSayHello} style={{ display: "flex", gap: 8 }}>
          <input
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Enter your name"
            style={{ flex: 1, padding: "8px 12px", border: "1px solid #ccc", borderRadius: 4 }}
          />
          <button
            type="submit"
            disabled={helloLoading || !name}
            style={{
              padding: "8px 16px",
              backgroundColor: helloLoading ? "#ccc" : "#2563eb",
              color: "#fff",
              border: "none",
              borderRadius: 4,
              cursor: helloLoading ? "default" : "pointer",
            }}
          >
            {helloLoading ? "Sending..." : "SayHello"}
          </button>
        </form>
        {helloResponse && (
          <div style={{ marginTop: 12, padding: 12, backgroundColor: "#f0fdf4", border: "1px solid #86efac", borderRadius: 4 }}>
            Response: {helloResponse}
          </div>
        )}
        {error && (
          <div style={{ marginTop: 12, padding: 12, backgroundColor: "#fef2f2", border: "1px solid #fca5a5", borderRadius: 4 }}>
            Error: {error}
          </div>
        )}
      </section>

      {/* --- Server Streaming RPC --- */}
      <section>
        <h2 style={{ fontSize: 18, marginBottom: 12 }}>Server Streaming: Subscribe</h2>
        <div style={{ display: "flex", gap: 8, marginBottom: 12 }}>
          <input
            type="text"
            value={clientName}
            onChange={(e) => setClientName(e.target.value)}
            placeholder="Client name"
            disabled={subscribed}
            style={{ flex: 1, padding: "8px 12px", border: "1px solid #ccc", borderRadius: 4 }}
          />
          {!subscribed ? (
            <button
              onClick={handleSubscribe}
              disabled={!clientName}
              style={{
                padding: "8px 16px",
                backgroundColor: !clientName ? "#ccc" : "#16a34a",
                color: "#fff",
                border: "none",
                borderRadius: 4,
                cursor: !clientName ? "default" : "pointer",
              }}
            >
              Subscribe
            </button>
          ) : (
            <button
              onClick={handleUnsubscribe}
              style={{
                padding: "8px 16px",
                backgroundColor: "#dc2626",
                color: "#fff",
                border: "none",
                borderRadius: 4,
                cursor: "pointer",
              }}
            >
              Unsubscribe
            </button>
          )}
        </div>

        {subscribed && (
          <div style={{ padding: 8, marginBottom: 8, backgroundColor: "#eff6ff", border: "1px solid #93c5fd", borderRadius: 4, fontSize: 14 }}>
            Listening for notifications...
          </div>
        )}

        {streamError && (
          <div style={{ padding: 12, marginBottom: 8, backgroundColor: "#fef2f2", border: "1px solid #fca5a5", borderRadius: 4 }}>
            Stream error: {streamError}
          </div>
        )}

        {events.length > 0 && (
          <div style={{ border: "1px solid #e5e7eb", borderRadius: 4, maxHeight: 300, overflowY: "auto" }}>
            {events.map((ev, i) => (
              <div
                key={i}
                style={{
                  padding: "8px 12px",
                  borderBottom: i < events.length - 1 ? "1px solid #f3f4f6" : "none",
                  fontSize: 14,
                }}
              >
                <span style={{ color: "#6b7280" }}>#{ev.sequence}</span>{" "}
                {ev.message}{" "}
                <span style={{ color: "#9ca3af", fontSize: 12 }}>{ev.timestamp}</span>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}
