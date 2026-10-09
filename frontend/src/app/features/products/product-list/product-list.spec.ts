import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { fakeAuth } from '../../../../testing/fake-auth';
import { aPage, aProduct } from '../../../../testing/product-fixtures';
import { Permission } from '../../../core/auth/auth';
import { ApiError } from '../../../core/http/api-error';
import { ProductsApi } from '../data-access/products-api';
import { PRODUCTS_AUTO_REFRESH_MS } from '../data-access/products-store';
import { ProductList } from './product-list';

describe('ProductList', () => {
  let api: { query: ReturnType<typeof vi.fn>; adjustStock: ReturnType<typeof vi.fn> };

  async function render(permissions?: Permission[]) {
    TestBed.configureTestingModule({
      providers: [
        fakeAuth(permissions),
        { provide: ProductsApi, useValue: api },
        { provide: PRODUCTS_AUTO_REFRESH_MS, useValue: 0 },
      ],
    });
    const fixture = TestBed.createComponent(ProductList);
    await fixture.whenStable();
    return fixture;
  }

  beforeEach(() => {
    api = { query: vi.fn(), adjustStock: vi.fn() };
  });

  it('renders products with their stock status', async () => {
    api.query.mockReturnValue(
      of(
        aPage([
          aProduct({ id: 100001, name: 'Progressive Lens', stockQuantity: 0 }),
          aProduct({ id: 100002, name: 'Frame', stockQuantity: 40 }),
        ]),
      ),
    );

    const fixture = await render();
    const rows = (fixture.nativeElement as HTMLElement).querySelectorAll(
      '[data-testid=product-row]',
    );

    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('#100001');
    expect(rows[0].textContent).toContain('Progressive Lens');
    expect(rows[0].textContent).toContain('Out of stock');
    expect(rows[1].textContent).toContain('In stock');
    const summary = (fixture.nativeElement as HTMLElement).querySelector(
      '[aria-label="Inventory summary"]',
    )!;
    expect(summary.textContent).toContain('Products');
    expect(summary.textContent).toContain('2');
  });

  it('disables the quick remove button when a product is out of stock', async () => {
    api.query.mockReturnValue(of(aPage([aProduct({ name: 'Frame', stockQuantity: 0 })])));

    const fixture = await render();
    const remove = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      'button[aria-label="Remove one unit of Frame"]',
    )!;

    expect(remove.disabled).toBe(true);
  });

  it('removes one unit through the API with the quick button', async () => {
    const product = aProduct({ name: 'Frame', stockQuantity: 5 });
    api.query.mockReturnValue(of(aPage([product])));
    api.adjustStock.mockReturnValue(of({ ...product, stockQuantity: 4 }));

    const fixture = await render();
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('button[aria-label="Remove one unit of Frame"]')!
      .click();
    await fixture.whenStable();

    expect(api.adjustStock).toHaveBeenCalledWith(product, 'remove', 1);
  });

  it('offers only what a viewer may do: no create, edit, delete or stock actions', async () => {
    api.query.mockReturnValue(of(aPage([aProduct({ name: 'Frame', stockQuantity: 5 })])));

    const fixture = await render(['products:read']);
    const element = fixture.nativeElement as HTMLElement;

    expect(element.textContent).toContain('Frame');
    expect(element.textContent).not.toContain('New product');
    expect(element.querySelector('button[aria-label="Remove one unit of Frame"]')).toBeNull();
    expect(element.querySelector('button[aria-label="Actions for Frame"]')).toBeNull();
  });

  it('lets a stock clerk change stock but not create products', async () => {
    api.query.mockReturnValue(of(aPage([aProduct({ name: 'Frame', stockQuantity: 5 })])));

    const fixture = await render(['products:read', 'stock:write']);
    const element = fixture.nativeElement as HTMLElement;

    expect(element.textContent).not.toContain('New product');
    expect(element.querySelector('button[aria-label="Remove one unit of Frame"]')).not.toBeNull();
  });

  it('shows an empty state when nothing matches', async () => {
    api.query.mockReturnValue(of(aPage([])));

    const fixture = await render();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'No products match the current filter.',
    );
  });

  it('shows the error and offers a retry when loading fails', async () => {
    api.query.mockReturnValue(
      throwError(() => new ApiError(0, 'network_error', 'The server could not be reached.')),
    );

    const fixture = await render();
    const alert = (fixture.nativeElement as HTMLElement).querySelector('[role=alert]')!;

    expect(alert.textContent).toContain('The server could not be reached.');
    expect(alert.textContent).toContain('Try again');
  });
});
