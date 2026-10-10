import { defineConfig } from '@playwright/test';

/**
 * End-to-end tests against a running system (by default the local cluster's gateway, http://localhost:8080):
 * the real gateway, API, Redis and PostgreSQL. Deploy the system first (the deploy repository:
 * `scripts/deploy.sh local`), or point BASE_URL at an environment. They use the local demo accounts from the README
 * and clean up what they create. Not part of CI: they need the whole system running.
 *
 *   npm run e2e
 *   BASE_URL=https://staging.example.com E2E_VIEWER_SECRET=... E2E_CLERK_SECRET=... E2E_MANAGER_SECRET=... npm run e2e
 */
export default defineConfig({
  testDir: './e2e',
  // One sign-in per role through the API, reused by every test (support.ts).
  globalSetup: './e2e/global-setup.ts',
  outputDir: './test-results',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  reporter: [['list'], ['html', { outputFolder: 'e2e/reports/playwright', open: 'never' }]],
  use: {
    baseURL: process.env['BASE_URL'] ?? 'http://localhost:8080',
    // The installed Chrome; where there is none (e.g. the Playwright container), its own Chromium: PLAYWRIGHT_CHANNEL=chromium.
    channel: process.env['PLAYWRIGHT_CHANNEL'] ?? 'chrome',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'desktop', use: { viewport: { width: 1280, height: 800 } } },
    {
      name: 'mobile',
      use: { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true },
    },
  ],
});
