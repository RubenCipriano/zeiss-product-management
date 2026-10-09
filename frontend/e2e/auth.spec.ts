import { expect, signIn, test } from './support';

test.describe('signing in', () => {
  test('a signed-out visitor is sent to the login page and back after signing in', async ({ page, consoleErrors }) => {
    await page.goto('/products');
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fproducts/);

    await page.getByLabel('Username').fill('viewer');
    await page.getByLabel('Password').fill(process.env['E2E_VIEWER_SECRET'] ?? 'viewer');
    await page.getByRole('button', { name: 'Sign in' }).click();

    await expect(page).toHaveURL(/\/products$/);
    expect(consoleErrors).toEqual([]);
  });

  test('wrong credentials are refused without saying which part was wrong', async ({ page }) => {
    await page.goto('/login');
    await page.getByLabel('Username').fill('manager');
    await page.getByLabel('Password').fill('not-the-secret');
    await page.getByRole('button', { name: 'Sign in' }).click();

    await expect(page.getByRole('alert')).toContainText(/invalid|incorrect|wrong/i);
    await expect(page).toHaveURL(/\/login/);
  });

  test('signing out ends the session', async ({ page }) => {
    await signIn(page, 'clerk');

    await page.getByRole('button', { name: 'Sign out' }).click();
    await page.goto('/products');

    await expect(page).toHaveURL(/\/login/);
  });
});

test.describe('roles', () => {
  test('a viewer can read and see the history, but change nothing', async ({ page }) => {
    await signIn(page, 'viewer');

    await expect(page.getByRole('button', { name: 'New product' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Add one unit of/ })).toHaveCount(0);
    await page.getByRole('button', { name: /^Actions for/ }).first().click();
    await expect(page.getByRole('menuitem')).toHaveText(['Stock history']);
  });

  test('a stock clerk can change stock but not create products', async ({ page }) => {
    await signIn(page, 'clerk');

    await expect(page.getByRole('button', { name: 'New product' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: /^Add one unit of/ }).first()).toBeVisible();
  });
});
