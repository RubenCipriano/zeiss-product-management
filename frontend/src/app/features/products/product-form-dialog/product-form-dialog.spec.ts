import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { aProduct } from '../../../../testing/product-fixtures';
import { ApiError } from '../../../core/http/api-error';
import { CategoriesApi } from '../data-access/categories-api';
import { Product } from '../data-access/product-models';
import { ProductsApi } from '../data-access/products-api';
import { ProductsStore } from '../data-access/products-store';
import { ProductFormDialog } from './product-form-dialog';

describe('ProductFormDialog', () => {
  let store: { create: ReturnType<typeof vi.fn>; update: ReturnType<typeof vi.fn> };
  let productsApi: { getById: ReturnType<typeof vi.fn> };
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  async function open(product?: Product) {
    store = { create: vi.fn(), update: vi.fn() };
    productsApi = { getById: vi.fn() };
    dialogRef = { close: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: { product } },
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: ProductsStore, useValue: store },
        { provide: ProductsApi, useValue: productsApi },
        {
          provide: CategoriesApi,
          useValue: { list: () => of([{ id: 1, name: 'Spectacle Lenses' }]) },
        },
      ],
    });
    const fixture = TestBed.createComponent(ProductFormDialog);
    await fixture.whenStable();
    return fixture;
  }

  type Fixture = Awaited<ReturnType<typeof open>>;

  function setField(fixture: Fixture, name: string, value: string) {
    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(
      `[formControlName=${name}]`,
    )!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function form(fixture: Fixture) {
    return (fixture.componentInstance as unknown as { form: ProductFormDialog['form'] }).form;
  }

  async function submit(fixture: Fixture) {
    (fixture.nativeElement as HTMLElement)
      .querySelector('form')!
      .dispatchEvent(new Event('submit'));
    await fixture.whenStable();
  }

  function text(fixture: Fixture): string {
    return (fixture.nativeElement as HTMLElement).textContent!;
  }

  it('does not submit an invalid form and explains what is wrong', async () => {
    const fixture = await open();

    setField(fixture, 'name', '   ');
    setField(fixture, 'price', '10.999');
    await submit(fixture);

    expect(store.create).not.toHaveBeenCalled();
    expect(text(fixture)).toContain('Name is required.');
    expect(text(fixture)).toContain('Price can have at most 2 decimal places.');
    expect(text(fixture)).toContain('Category is required.');
  });

  it('creates a product with trimmed values and closes with the result', async () => {
    const fixture = await open();
    const created = aProduct({ id: 123456 });
    store.create.mockReturnValue(of(created));

    form(fixture).setValue({
      name: '  Frame ',
      description: '  ',
      price: 10.5,
      stockQuantity: 3,
      categoryId: 1,
    });
    await submit(fixture);

    expect(store.create).toHaveBeenCalledWith({
      name: 'Frame',
      description: null,
      price: 10.5,
      stockQuantity: 3,
      categoryId: 1,
    });
    expect(dialogRef.close).toHaveBeenCalledWith(created);
  });

  it('shows server-side validation errors on the matching fields', async () => {
    const fixture = await open();
    store.create.mockReturnValue(
      throwError(
        () =>
          new ApiError(400, 'validation_failed', 'Invalid', {
            categoryId: ['Category 1 does not exist.'],
          }),
      ),
    );

    form(fixture).setValue({
      name: 'Frame',
      description: '',
      price: 10,
      stockQuantity: 3,
      categoryId: 1,
    });
    await submit(fixture);

    expect(text(fixture)).toContain('Category 1 does not exist.');
    expect(dialogRef.close).not.toHaveBeenCalled();
  });

  it('pre-fills the form when editing and sends the edited version', async () => {
    const product = aProduct({ name: 'Old name', version: 7 });
    const fixture = await open(product);
    store.update.mockReturnValue(of({ ...product, name: 'New name', version: 8 }));

    expect(form(fixture).controls.name.value).toBe('Old name');
    form(fixture).controls.name.setValue('New name');
    await submit(fixture);

    expect(store.update).toHaveBeenCalledWith(
      product,
      expect.objectContaining({ name: 'New name' }),
    );
  });

  it('on a 412 explains the conflict and can reload the latest version', async () => {
    const product = aProduct({ stockQuantity: 10, version: 7 });
    const fixture = await open(product);
    store.update.mockReturnValue(
      throwError(() => new ApiError(412, 'product.version_mismatch', 'Modified')),
    );
    const latest = { ...product, stockQuantity: 9, version: 8 };
    productsApi.getById.mockReturnValue(of(latest));

    await submit(fixture);
    expect(text(fixture)).toContain('changed by someone else');

    const reload = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find(
      (b) => b.textContent?.includes('Reload latest'),
    )!;
    reload.click();
    await fixture.whenStable();

    expect(productsApi.getById).toHaveBeenCalledWith(product.id);
    expect(form(fixture).controls.stockQuantity.value).toBe(9);
    expect(text(fixture)).not.toContain('changed by someone else');
  });
});
