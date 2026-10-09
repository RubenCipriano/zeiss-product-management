import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { aProduct } from '../../../../testing/product-fixtures';
import { ApiError } from '../../../core/http/api-error';
import { ProductsStore } from '../data-access/products-store';
import { StockAdjustDialog } from './stock-adjust-dialog';

describe('StockAdjustDialog', () => {
  const product = aProduct({ stockQuantity: 9 });
  let store: { adjustStock: ReturnType<typeof vi.fn> };
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  async function open() {
    store = { adjustStock: vi.fn() };
    dialogRef = { close: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: { product, operation: 'remove' } },
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: ProductsStore, useValue: store },
      ],
    });
    const fixture = TestBed.createComponent(StockAdjustDialog);
    await fixture.whenStable();
    return fixture;
  }

  async function enterQuantity(fixture: Awaited<ReturnType<typeof open>>, quantity: number) {
    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      'input[type=number]',
    )!;
    input.value = String(quantity);
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  function submit(fixture: Awaited<ReturnType<typeof open>>) {
    (fixture.nativeElement as HTMLElement)
      .querySelector('form')!
      .dispatchEvent(new Event('submit'));
  }

  it('previews the resulting stock and warns when it would go below zero', async () => {
    const fixture = await open();

    await enterQuantity(fixture, 12);

    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Stock after this change: -3');
    expect(text).toContain('not enough stock');
  });

  it('applies the change through the store and closes with the updated product', async () => {
    const fixture = await open();
    const updated = { ...product, stockQuantity: 6 };
    store.adjustStock.mockReturnValue(of(updated));

    await enterQuantity(fixture, 3);
    submit(fixture);

    expect(store.adjustStock).toHaveBeenCalledWith(product, 'remove', 3);
    expect(dialogRef.close).toHaveBeenCalledWith(updated);
  });

  it("shows the API's explanation when the change is rejected", async () => {
    const fixture = await open();
    store.adjustStock.mockReturnValue(
      throwError(() => new ApiError(409, 'product.insufficient_stock', 'Only 9 available.')),
    );

    await enterQuantity(fixture, 5);
    submit(fixture);
    await fixture.whenStable();

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[role=alert]')!.textContent,
    ).toContain('Only 9 available.');
    expect(dialogRef.close).not.toHaveBeenCalled();
  });

  it('does not call the API for an invalid quantity', async () => {
    const fixture = await open();

    await enterQuantity(fixture, 0);
    submit(fixture);

    expect(store.adjustStock).not.toHaveBeenCalled();
  });
});
