import { expect, signIn, test } from './support';

test.describe('managing products (manager)', () => {
  test('create, find, change stock by 10, see the history, edit and delete', async ({ page, consoleErrors }) => {
    const name = `E2E lens ${Date.now()}-${test.info().project.name}`;
    await signIn(page, 'manager');

    // Create
    await page.getByRole('button', { name: 'New product' }).click();
    const form = page.getByRole('dialog', { name: 'New product' });
    await form.getByLabel('Name').fill(name);
    await form.getByLabel('Price').fill('19.90');
    await form.getByLabel('Stock').fill('5');
    await form.getByLabel('Category').selectOption({ index: 1 });
    await form.getByRole('button', { name: /Create/ }).click();
    await expect(form).toHaveCount(0);

    // Find it with a partial, case-insensitive search
    await page.getByRole('radio', { name: 'Search by name' }).click();
    await page.getByLabel('Name contains').fill(name.slice(4, 20).toUpperCase());
    const row = page.getByTestId('product-row').filter({ hasText: name });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('5');

    // +10 in one click
    await page.getByLabel('Units per + / - click').selectOption('10');
    await row.getByRole('button', { name: `Add 10 units of ${name}` }).click();
    await expect(row).toContainText('15');
    // -10 is refused by the UI when there is not enough stock... 15 is enough:
    await row.getByRole('button', { name: `Remove 10 units of ${name}` }).click();
    await expect(row).toContainText('5');

    // The history records each change, newest first (an accessible, named dialog)
    await row.getByRole('button', { name: `Actions for ${name}` }).click();
    await page.getByRole('menuitem', { name: 'Stock history' }).click();
    const history = page.getByRole('dialog', { name: 'Stock history' });
    await expect(history.getByTestId('movement-row')).toHaveCount(3);
    await expect(history.getByTestId('movement-row').first()).toContainText('-10');
    await history.getByRole('button', { name: 'Close' }).click();

    // Edit
    await row.getByRole('button', { name: `Actions for ${name}` }).click();
    await page.getByRole('menuitem', { name: 'Edit' }).click();
    const edit = page.getByRole('dialog', { name: /Edit/ });
    await edit.getByLabel('Price').fill('24.50');
    await edit.getByRole('button', { name: /Save/ }).click();
    await expect(row).toContainText('24.50');

    // Delete, after confirming
    await row.getByRole('button', { name: `Actions for ${name}` }).click();
    await page.getByRole('menuitem', { name: 'Delete' }).click();
    const confirm = page.getByRole('alertdialog', { name: 'Delete product?' });
    await confirm.getByRole('button', { name: 'Delete' }).click();
    await expect(row).toHaveCount(0);

    expect(consoleErrors).toEqual([]);
  });
});
