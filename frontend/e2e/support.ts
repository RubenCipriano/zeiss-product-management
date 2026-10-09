import { expect, Page, test as base } from '@playwright/test';

export type Role = 'viewer' | 'clerk' | 'manager';

/** Local demo accounts (README). Override per environment with E2E_<ROLE>_SECRET. */
const secretFor = (role: Role) => process.env[`E2E_${role.toUpperCase()}_SECRET`] ?? role;

export async function signIn(page: Page, role: Role): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill(role);
  await page.getByLabel('Password').fill(secretFor(role));
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/\/products/);
  await expect(page.getByTestId('product-row').first()).toBeVisible();
}

/** Fails the test if the page reports a Content-Security-Policy violation or any uncaught error. */
export const test = base.extend<{ consoleErrors: string[] }>({
  consoleErrors: async ({ page }, use) => {
    const errors: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && /Content Security Policy|Refused to/i.test(message.text())) {
        errors.push(message.text());
      }
    });
    page.on('pageerror', (error) => errors.push(error.message));
    await use(errors);
    expect(errors, 'CSP violations or uncaught errors').toEqual([]);
  },
});

export { expect };
