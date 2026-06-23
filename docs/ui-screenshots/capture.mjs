// UI スナップショット撮影スクリプト（再現用）。
//
// 前提:
//   1) OSS スタック起動 + API(:5000) + web-client dev(:3000)
//        docker compose -f docker-compose.oss.yaml up -d      # api/connector 含む
//        (cd web-client && NEXT_PUBLIC_API_BASE_URL=http://localhost:5000 yarn dev)
//   2) デモ用ツインを OxiGraph に投入（統合テストの sbco-sample.ttl を default graph へ）
//        curl -X PUT -H 'Content-Type: text/turtle' \
//          --data-binary @DotNet/BuildingOS.IntegrationTest/Common/Fixtures/SeedData/sbco-sample.ttl \
//          'http://localhost:7878/store?default'
//   3) playwright-core + chromium（npm i playwright-core / npx playwright install chromium）
//
// 実行:
//   PW_CHROME=<chrome path> WEB=http://localhost:3000 OUT=docs/ui-screenshots \
//     node docs/ui-screenshots/capture.mjs
//
// 認証: フロントの middleware は oidc.access_token クッキーの存在のみ検査し署名検証しない
// （web-client/src/lib/auth/claims.ts）。API は compose で DISABLE_AUTH=true。よって署名なし JWT
// （building_os_role=admin）をクッキー注入すれば保護ルートを撮影できる（デモ/レビュー用途限定）。

import pkg from 'playwright-core';
const { chromium } = pkg;
import fs from 'node:fs';

const EXEC = process.env.PW_CHROME; // chromium 実体パス（未設定なら playwright 既定解決）
const WEB = process.env.WEB || 'http://localhost:3000';
const OUT = process.env.OUT || 'docs/ui-screenshots';
fs.mkdirSync(OUT, { recursive: true });

const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
const token = `${b64({ alg: 'none', typ: 'JWT' })}.${b64({
  building_os_role: 'admin', permissions: ['*'], preferred_username: 'demo', name: 'Demo Admin',
  exp: Math.floor(Date.now() / 1000) + 86400,
})}.sig`;

const launchOpts = EXEC
  ? { executablePath: EXEC, headless: true, args: ['--no-sandbox'] }
  : { headless: true, args: ['--no-sandbox'] };
const browser = await chromium.launch(launchOpts);

// (A) sign-in: クッキー無し
const a = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const pa = await a.newPage();
await pa.goto(`${WEB}/sign-in`, { waitUntil: 'networkidle', timeout: 30000 }).catch(() => {});
await pa.waitForTimeout(1500);
await pa.screenshot({ path: `${OUT}/01-sign-in.png`, fullPage: true });
await a.close();

// (B) 認証済み（クッキー注入）
const b = await browser.newContext({ viewport: { width: 1440, height: 900 } });
await b.addCookies([{ name: 'oidc.access_token', value: token, domain: 'localhost', path: '/' }]);
const p = await b.newPage();

await p.goto(`${WEB}/resources`, { waitUntil: 'networkidle', timeout: 30000 }).catch(() => {});
await p.waitForTimeout(2500);
await p.getByText('bldg-1', { exact: true }).first().click({ timeout: 5000 }).catch(() => {});
await p.waitForTimeout(2500);
await p.screenshot({ path: `${OUT}/02-resources-explorer.png`, fullPage: true });

await p.goto(`${WEB}/platform/status`, { waitUntil: 'load', timeout: 30000 }).catch(() => {});
// dev の React StrictMode 二重マウントで初回 fetch が abort され、inFlight ガードにより
// 次の自動更新（15s 間隔）まで「読み込み中」になる。確実に実データを描画させるため 18s 待つ。
await p.waitForTimeout(18000);
await p.screenshot({ path: `${OUT}/04-platform-status.png`, fullPage: true });

await p.goto(`${WEB}/admin/users`, { waitUntil: 'networkidle', timeout: 30000 }).catch(() => {});
await p.waitForTimeout(3000);
await p.screenshot({ path: `${OUT}/05-admin-users.png`, fullPage: true });

await browser.close();
console.log('done');
