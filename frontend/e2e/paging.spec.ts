import { expect, signIn, test } from './support';

test('paging shows new rows, and pages already seen come from the browser cache', async ({ page }) => {
  await signIn(page, 'viewer');
  const firstId = await page.getByTestId('product-row').first().locator('td').first().textContent();

  const listRequests: string[] = [];
  page.on('request', (request) => {
    if (/\/api\/products\?page=/.test(request.url())) listRequests.push(request.url());
  });

  for (let i = 0; i < 3; i++) {
    await page.getByRole('button', { name: 'Next page' }).click();
  }
  await expect(page.locator('[aria-busy="false"]')).toBeVisible();
  await expect(page.getByTestId('product-row').first().locator('td').first()).not.toHaveText(firstId!);

  const forward = listRequests.length;
  for (let i = 0; i < 3; i++) {
    await page.getByRole('button', { name: 'Previous page' }).click();
  }
  await expect(page.getByTestId('product-row').first().locator('td').first()).toHaveText(firstId!);

  // Going back revisits pages seen seconds ago: no new request for them.
  expect(listRequests.length - forward).toBe(0);
});
