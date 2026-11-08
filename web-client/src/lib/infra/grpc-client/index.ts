import type { Interceptor } from "@connectrpc/connect";
import { createGrpcWebTransport } from "@connectrpc/connect-web";
import Cookies from "js-cookie";

const baseUrl = process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";

export const grpcTransport = createGrpcWebTransport({ baseUrl });

/**
 * Sends the Keycloak access token like the REST clients do (#636). The latest-value stream is authorized
 * as a point read, so — unlike the control-result stream, which is keyed by an unguessable control id — it
 * needs the caller's identity. The token is read per call, so a refreshed cookie is picked up on reconnect.
 */
const bearerToken: Interceptor = (next) => async (req) => {
  const token = Cookies.get("oidc.access_token");
  if (token) req.header.set("Authorization", `Bearer ${token}`);
  return next(req);
};

export const authenticatedGrpcTransport = createGrpcWebTransport({
  baseUrl,
  interceptors: [bearerToken],
});
