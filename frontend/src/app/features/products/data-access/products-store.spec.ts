import { TestBed } from '@angular/core/testing';
import { ApplicationRef } from '@angular/core';
import { of, throwError } from 'rxjs';
import { aPage, aProduct } from '../../../../testing/product-fixtures';
import { ApiError } from '../../../core/http/api-error';
import { ProductsApi } from './products-api';
import { PRODUCTS_AUTO_REFRESH_MS, ProductsStore } from './products-store';

describe('ProductsStore', () => {
  const lens = aProduct({ id: 100001, stockQuantity: 5 });
  const frame = aProduct({ id: 100002, name: 'Frame', stockQuantity: 9 });
  let api: { query: ReturnType<typeof vi.fn>; adjustStock: ReturnType<typeof vi.fn> };
  let store: ProductsStore;

  beforeEach(() => {
    api = {
      query: vi.fn(() => of(aPage([lens, frame]))),
      adjustStock: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        ProductsStore,
        { provide: ProductsApi, useValue: api },
        { provide: PRODUCTS_AUTO_REFRESH_MS, useValue: 0 },
      ],
    });
    store = TestBed.inject(ProductsStore);
  });

  const settle = () => TestBed.inject(ApplicationRef).whenStable();

  it('loads the first page of all products', async () => {
    await settle();

    expect(api.query).toHaveBeenCalledWith({ filter: { kind: 'all' }, page: 1, pageSize: 20 });
    expect(store.products()).toEqual([lens, frame]);
    expect(store.totalCount()).toBe(2);
  });

  it('resets to the first page when the filter changes', async () => {
    store.setPage(3, 20);
    store.setFilter({ kind: 'name', name: 'lens' });
    await settle();

    expect(api.query).toHaveBeenLastCalledWith({
      filter: { kind: 'name', name: 'lens' },
      page: 1,
      pageSize: 20,
    });
  });

  it('does not re-query when the same filter is applied again', async () => {
    await settle();
    const calls = api.query.mock.calls.length;

    store.setFilter({ kind: 'all' });
    await settle();

    expect(api.query.mock.calls.length).toBe(calls);
  });

  it('shows a stock change immediately and then re-fetches', async () => {
    await settle();
    const updated = { ...lens, stockQuantity: 4, version: 8 };
    api.adjustStock.mockReturnValue(of(updated));
    api.query.mockReturnValue(of(aPage([updated, frame])));
    const calls = api.query.mock.calls.length;

    store.adjustStock(lens, 'remove', 1).subscribe();

    expect(store.products()[0].stockQuantity).toBe(4);
    await settle();
    expect(api.query.mock.calls.length).toBe(calls + 1);
  });

  it('exposes load failures as ApiError', async () => {
    api.query.mockReturnValue(throwError(() => new ApiError(0, 'network_error', 'Offline')));
    store.setFilter({ kind: 'stock', min: 0, max: 0 });
    await settle();

    expect(store.error()?.code).toBe('network_error');
    expect(store.products()).toEqual([]);
  });
});
