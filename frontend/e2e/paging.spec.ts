import { expect, signIn, test } from './support';

test('paging shows new rows, and pages already seen appear at once from the browser cache', async ({
  page,
}) => {
  await signIn(page, 'viewer');
  const firstId = await page.getByTestId('product-row').first().locator('td').first().textContent();

  for (let i = 0; i < 3; i++) {
    await page.getByRole('button', { name: 'Next page' }).click();
  }
  await expect(page.locator('[aria-busy="false"]')).toBeVisible();
  await expect(page.getByTestId('product-row').first().locator('td').first()).not.toHaveText(
    firstId!,
  );

  // From now on the network answers 3 s late. Pages already seen must still appear at once: they come from the
  // browser cache (fresh, or shown at once and refreshed behind the scenes after 5 s).
  await page.route(/\/api\/products\?page=/, async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 3000));
    await route.continue();
  });
  for (let i = 0; i < 3; i++) {
    await page.getByRole('button', { name: 'Previous page' }).click();
  }
  await expect(page.getByTestId('product-row').first().locator('td').first()).toHaveText(firstId!, {
    timeout: 1500,
  });
});
