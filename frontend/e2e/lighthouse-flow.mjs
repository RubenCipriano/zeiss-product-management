// Lighthouse user flow over the real journeys: the login page, signing in, the product list signed in, paging and
// the stock history dialog. A plain `lighthouse <url>` cannot audit signed-in pages: the session lives in
// sessionStorage (per tab), so the flow signs in through the UI and keeps the same tab.
//
//   npm run audit:lighthouse                        # mobile (Lighthouse default), against http://localhost:8080
//   FORM_FACTOR=desktop npm run audit:lighthouse
//
// Writes e2e/reports/lighthouse-<form factor>.html (the full report) and prints the scores.
import { mkdirSync, writeFileSync } from 'node:fs';
import { startFlow, desktopConfig } from 'lighthouse';
import puppeteer from 'puppeteer-core';

const BASE_URL = (process.env.BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '');
const FORM_FACTOR = process.env.FORM_FACTOR ?? 'mobile';
// Local demo account (README). Never point this at a real environment with real credentials in the shell.
const USERNAME = process.env.E2E_USERNAME ?? 'manager';
const PASSWORD = process.env.E2E_PASSWORD ?? 'manager';
const CHROME =
  process.env.CHROME_PATH ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';

const browser = await puppeteer.launch({ executablePath: CHROME, headless: true });
const page = await browser.newPage();
const flow = await startFlow(page, {
  name: `Product Management (${FORM_FACTOR})`,
  config: FORM_FACTOR === 'desktop' ? desktopConfig : undefined,
});

await flow.navigate(`${BASE_URL}/login`, { name: 'Login page (cold load)' });

await flow.startTimespan({ name: 'Sign in' });
await page.type('#username', USERNAME);
await page.type('#password', PASSWORD);
await Promise.all([page.waitForSelector('tbody tr'), page.click('button[type=submit]')]);
await flow.endTimespan();

// Same tab, storage kept: the session survives the reload.
await flow.navigate(`${BASE_URL}/products`, { name: 'Products, signed in', disableStorageReset: true });
await page.waitForSelector('tbody tr');

await flow.startTimespan({ name: 'Paging through 5 pages' });
for (let i = 0; i < 5; i++) {
  await page.$eval('button[aria-label="Next page"]', (button) => button.click());
  await page.waitForSelector('[aria-busy="false"]');
}
await flow.endTimespan();

await page.$eval('button[aria-label^="Actions for"]', (button) => button.click());
await page.waitForSelector('[role=menuitem]');
await page.$$eval('[role=menuitem]', (items) => items.find((item) => item.textContent.includes('Stock history')).click());
await page.waitForSelector('[data-testid=movement-row]');
// A snapshot has no load metrics: performance does not apply (Lighthouse would show 0).
await flow.snapshot({
  name: 'Stock history dialog',
  flags: { onlyCategories: ['accessibility', 'best-practices'] },
});

await browser.close();

mkdirSync('e2e/reports', { recursive: true });
writeFileSync(`e2e/reports/lighthouse-${FORM_FACTOR}.html`, await flow.generateReport());

const result = await flow.createFlowResult();
writeFileSync(`e2e/reports/lighthouse-${FORM_FACTOR}.json`, JSON.stringify(result));
for (const step of result.steps) {
  const scores = Object.values(step.lhr.categories)
    .map((category) => `${category.title} ${Math.round((category.score ?? 0) * 100)}`)
    .join(' · ');
  console.log(`${step.name.padEnd(28)} ${scores}`);
}
const failing = result.steps.flatMap((step) =>
  Object.values(step.lhr.audits)
    .filter((audit) => audit.score !== null && audit.score < 0.9 && audit.scoreDisplayMode !== 'informative')
    .map((audit) => `  [${step.name}] ${audit.id}: ${audit.title}${audit.displayValue ? ` (${audit.displayValue})` : ''}`),
);
console.log(failing.length ? `\nAudits below 90:\n${[...new Set(failing)].join('\n')}` : '\nNo audit below 90.');
