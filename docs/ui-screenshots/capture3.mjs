import pkg from '/tmp/pwcap/node_modules/playwright-core/index.js';
const { chromium } = pkg;

const EXEC = '/home/takashi/.cache/ms-playwright/chromium-1223/chrome-linux64/chrome';
const OUT = '/home/takashi/projects/gutp/gutp-building-os-oss/docs/ui-screenshots';

const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
const token = `${b64({ alg: 'none', typ: 'JWT' })}.${b64({
  building_os_role: 'admin', permissions: ['*'], preferred_username: 'demo', name: 'Demo Admin',
  exp: Math.floor(Date.now() / 1000) + 86400,
})}.sig`;

const browser = await chromium.launch({ executablePath: EXEC, headless: true, args: ['--no-sandbox'] });
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
await ctx.addCookies([{ name: 'oidc.access_token', value: token, domain: 'localhost', path: '/' }]);
const page = await ctx.newPage();

async function shot(name) { await page.screenshot({ path: `${OUT}/${name}.png`, fullPage: true }); console.log('OK', name); }

// ── twin: run a real SELECT + a real import preview ──
await page.goto('http://localhost:3000/admin/twin', { waitUntil: 'networkidle', timeout: 30000 }).catch(() => {});
await page.waitForTimeout(1500);
try {
  await page.getByTestId('sparql-input').fill(
    'PREFIX sbco: <https://www.sbco.or.jp/ont/>\nSELECT ?point ?gw WHERE {\n  ?point a sbco:PointExt ; sbco:gatewayId ?gw .\n} ORDER BY ?gw LIMIT 20');
  await page.getByTestId('run-query').click();
  await page.waitForTimeout(1800);
  await page.getByTestId('ttl-input').fill(
    '@prefix sbco: <https://www.sbco.or.jp/ont/> .\n\n<urn:pt:PT900> a sbco:PointExt ;\n  sbco:id "PT900" ;\n  sbco:gatewayId "GW001" ;\n  sbco:building "bldg-1" .');
  await page.getByTestId('preview-button').click();
  await page.waitForTimeout(1800);
} catch (e) { console.log('twin interact:', e.message); }
await shot('06-admin-twin');

// ── gateways (real GW001/GW002) ──
await page.goto('http://localhost:3000/admin/gateways', { waitUntil: 'networkidle', timeout: 30000 }).catch(() => {});
await page.waitForTimeout(2500);
await shot('07-admin-gateways');

// ── oidc (clean 503 unconfigured state) ──
await page.goto('http://localhost:3000/admin/oidc-clients', { waitUntil: 'networkidle', timeout: 30000 }).catch(() => {});
await page.waitForTimeout(2500);
await shot('08-admin-oidc-clients');

// ── users (Keycloak realm-management dependent) ──
await page.goto('http://localhost:3000/admin/users', { waitUntil: 'networkidle', timeout: 30000 }).catch(() => {});
await page.waitForTimeout(2500);
await shot('09-admin-users-roles');

await browser.close();
console.log('done');
