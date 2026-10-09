import { defineConfig } from '@playwright/test';

/**
 * End-to-end tests against a running system (by default the local cluster's gateway, http://localhost:8080):
 * the real gateway, API, Redis and PostgreSQL. Run `scripts/deploy-all.sh` first. They use the local demo accounts
 * from the README and clean up what they create.
 *
 *   npm run e2e
 *   BASE_URL=https://staging.example.com E2E_MANAGER_SECRET=... npm run e2e
 */
export default defineConfig({
  testDir: './e2e',
  outputDir: './test-results',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  reporter: [['list'], ['html', { outputFolder: 'e2e/reports/playwright', open: 'never' }]],
  use: {
    baseURL: process.env['BASE_URL'] ?? 'http://localhost:8080',
    channel: process.env['PLAYWRIGHT_CHANNEL'] ?? 'chrome',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'desktop', use: { viewport: { width: 1280, height: 800 } } },
    { name: 'mobile', use: { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true } },
  ],
});
