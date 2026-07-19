FROM node:22-alpine AS base

FROM base AS deps
WORKDIR /app
COPY package.json yarn.lock ./
RUN yarn install --frozen-lockfile --ignore-engines

FROM base AS builder
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
# NEXT_PUBLIC_* は Next.js がブラウザバンドルにビルド時へ焼き込む公開変数のため、実行時 env では
# なく build args として渡す必要がある。既定値はローカル開発（ホストマップされたポート）に一致。
ARG NEXT_PUBLIC_API_BASE_URL=http://localhost:5000
ARG NEXT_PUBLIC_KEYCLOAK_AUTHORITY=http://localhost:8080/realms/building-os
ARG NEXT_PUBLIC_KEYCLOAK_CLIENT_ID=web-client
ARG NEXT_PUBLIC_ASSISTANT_ENABLED=false
# デモプロファイル限定の自動ログイン（#161）。既定 false（本番/開発では実 Keycloak ログイン）。
ARG NEXT_PUBLIC_DEMO_MODE=false
ENV NEXT_PUBLIC_API_BASE_URL=${NEXT_PUBLIC_API_BASE_URL} \
    NEXT_PUBLIC_KEYCLOAK_AUTHORITY=${NEXT_PUBLIC_KEYCLOAK_AUTHORITY} \
    NEXT_PUBLIC_KEYCLOAK_CLIENT_ID=${NEXT_PUBLIC_KEYCLOAK_CLIENT_ID} \
    NEXT_PUBLIC_ASSISTANT_ENABLED=${NEXT_PUBLIC_ASSISTANT_ENABLED} \
    NEXT_PUBLIC_DEMO_MODE=${NEXT_PUBLIC_DEMO_MODE}
RUN yarn build && mkdir -p public

FROM base AS runner
WORKDIR /app
ENV NODE_ENV=production
ENV PORT=3000

RUN addgroup --system --gid 1001 nodejs
RUN adduser --system --uid 1001 nextjs

COPY --from=builder /app/public ./public
COPY --from=builder --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/.next/static ./.next/static

USER nextjs
EXPOSE 3000

CMD ["node", "server.js"]
