import { createGrpcWebTransport } from "@connectrpc/connect-web";

export const grpcTransport = createGrpcWebTransport({
  baseUrl: process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000",
});
