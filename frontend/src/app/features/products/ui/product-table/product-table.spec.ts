import { TestBed } from '@angular/core/testing';
import { aProduct } from '../../../../../testing/product-fixtures';
import { Product } from '../../data-access/product-models';
import { ProductTable, QuickStockChange } from './product-table';

describe('ProductTable', () => {
  const lens = aProduct({ id: 100001, name: 'Lens', stockQuantity: 12 });
  const frame = aProduct({ id: 100002, name: 'Frame', stockQuantity: 3 });

  async function render(
    inputs: {
      canChangeStock?: boolean;
      canEditProducts?: boolean;
      pendingIds?: ReadonlySet<number>;
    } = {},
  ) {
    const fixture = TestBed.createComponent(ProductTable);
    fixture.componentRef.setInput('products', [lens, frame]);
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    const changes: QuickStockChange[] = [];
    fixture.componentInstance.quickChange.subscribe((change) => changes.push(change));
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const button = (label: string) =>
      element.querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`);
    return { fixture, element, changes, button };
  }

  async function openMenu(
    fixture: Awaited<ReturnType<typeof render>>['fixture'],
    product: Product,
  ) {
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>(`button[aria-label="Actions for ${product.name}"]`)!
      .click();
    await fixture.whenStable();
    return [...document.querySelectorAll('[role=menuitem]')].map((item) =>
      item.textContent!.trim(),
    );
  }

  it('names the table for screen readers', async () => {
    const { element } = await render();

    expect(element.querySelector('caption')!.textContent!.trim()).toBe(
      'Products matching the current filter',
    );
  });

  it('emits a quick change with the chosen step', async () => {
    const { fixture, element, changes, button } = await render({ canChangeStock: true });

    button('Add one unit of Lens')!.click();
    const step = element.querySelector<HTMLSelectElement>('[data-testid=stock-step]')!;
    step.value = '5';
    step.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    button('Remove 5 units of Lens')!.click();

    expect(changes).toEqual([
      { product: lens, operation: 'add', quantity: 1 },
      { product: lens, operation: 'remove', quantity: 5 },
    ]);
  });

  it('cannot remove more units than are in stock', async () => {
    const { fixture, element, button } = await render({ canChangeStock: true });

    const step = element.querySelector<HTMLSelectElement>('[data-testid=stock-step]')!;
    step.value = '5';
    step.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(button('Remove 5 units of Frame')!.disabled).toBe(true);
    expect(button('Remove 5 units of Lens')!.disabled).toBe(false);
  });

  it('disables the quick buttons of rows with a stock change in flight', async () => {
    const { button } = await render({ canChangeStock: true, pendingIds: new Set([frame.id]) });

    expect(button('Remove one unit of Frame')!.disabled).toBe(true);
    expect(button('Add one unit of Frame')!.disabled).toBe(true);
    expect(button('Remove one unit of Lens')!.disabled).toBe(false);
    expect(button('Add one unit of Lens')!.disabled).toBe(false);
  });

  it('offers only the stock history without any write permission', async () => {
    const { fixture, element, button } = await render();

    expect(element.querySelector('[data-testid=stock-step]')).toBeNull();
    expect(button('Add one unit of Lens')).toBeNull();
    expect(await openMenu(fixture, lens)).toEqual(['Stock history']);
  });

  it('adds stock actions for stock changes, and edit and delete for product changes', async () => {
    const stockOnly = await render({ canChangeStock: true });
    expect(await openMenu(stockOnly.fixture, lens)).toEqual(['Stock history', 'Adjust stock…']);

    TestBed.resetTestingModule();
    const productsOnly = await render({ canEditProducts: true });
    expect(productsOnly.button('Add one unit of Lens')).toBeNull();
    expect(await openMenu(productsOnly.fixture, lens)).toEqual(['Stock history', 'Edit', 'Delete']);
  });

  it('reports the menu action with its product', async () => {
    const { fixture } = await render({ canEditProducts: true });
    const edited: Product[] = [];
    fixture.componentInstance.edit.subscribe((product) => edited.push(product));

    await openMenu(fixture, frame);
    [...document.querySelectorAll<HTMLButtonElement>('[role=menuitem]')]
      .find((item) => item.textContent!.trim() === 'Edit')!
      .click();

    expect(edited).toEqual([frame]);
  });

  it('shows placeholders while the first page loads, and an empty state otherwise', async () => {
    const fixture = TestBed.createComponent(ProductTable);
    fixture.componentRef.setInput('products', []);
    fixture.componentRef.setInput('loading', true);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelectorAll('.animate-pulse').length).toBe(5);

    fixture.componentRef.setInput('loading', false);
    await fixture.whenStable();

    expect(element.querySelector('.animate-pulse')).toBeNull();
    expect(element.textContent).toContain('No products match the current filter.');
  });
});
