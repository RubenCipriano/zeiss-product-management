import { TestBed } from '@angular/core/testing';
import { ApplicationRef } from '@angular/core';
import { Observable, of, Subject, throwError } from 'rxjs';
import { aPage, aProduct } from '../../../../testing/product-fixtures';
import { PagedResponse } from '../../../shared/models/paged-response';
import { Product, ProductQuery } from './product-models';
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
  /** Requests for the product table (the summary cards also call `query`, with pageSize 1). */
  const listRequests = () => api.query.mock.calls.filter(([query]) => query.pageSize !== 1).length;

  it('loads the first page of all products', async () => {
    await settle();

    expect(api.query).toHaveBeenCalledWith(
      { filter: { kind: 'all' }, page: 1, pageSize: 20 },
      false,
    );
    expect(store.products()).toEqual([lens, frame]);
    expect(store.totalCount()).toBe(2);
  });

  it('prefetches the next page once a page has loaded', async () => {
    api.query.mockImplementation((query: ProductQuery) =>
      of(aPage([lens, frame], { page: query.page, totalCount: 50 })),
    );

    await settle();

    expect(api.query).toHaveBeenCalledWith({ filter: { kind: 'all' }, page: 2, pageSize: 20 });
  });

  it('does not prefetch past the last page', async () => {
    await settle(); // 2 products: a single page

    expect(api.query.mock.calls.some(([query]) => query.page === 2)).toBe(false);
  });

  it('reports a page change while the new page loads, but not a background refresh of the same page', async () => {
    await settle();
    const nextPage = new Subject<PagedResponse<Product>>();
    api.query.mockReturnValue(nextPage);

    store.reload({ fresh: false });
    TestBed.tick();
    expect(store.isLoading()).toBe(true);
    expect(store.isChangingPage()).toBe(false);

    store.setPage(2, 20);
    TestBed.tick();
    expect(store.isChangingPage()).toBe(true);
    expect(store.products()).toEqual([lens, frame]); // the previous rows stay on screen meanwhile
  });

  it('resets to the first page when the filter changes', async () => {
    store.setPage(3, 20);
    store.setFilter({ kind: 'name', name: 'lens' });
    await settle();

    const tableRequests = api.query.mock.calls.filter(([query]) => query.pageSize !== 1);
    expect(tableRequests.at(-1)?.[0]).toEqual({
      filter: { kind: 'name', name: 'lens' },
      page: 1,
      pageSize: 20,
    });
  });

  it('does not re-query when the same filter is applied again', async () => {
    await settle();
    const calls = listRequests();

    store.setFilter({ kind: 'all' });
    await settle();

    expect(listRequests()).toBe(calls);
  });

  it('shows a stock change immediately and then re-fetches', async () => {
    await settle();
    const updated = { ...lens, stockQuantity: 4, version: 8 };
    api.adjustStock.mockReturnValue(of(updated));
    api.query.mockReturnValue(of(aPage([updated, frame])));
    const calls = listRequests();

    store.adjustStock(lens, 'remove', 1, 'key').subscribe();

    expect(store.products()[0].stockQuantity).toBe(4);
    await settle();
    expect(listRequests()).toBe(calls + 1);
  });

  it('keeps showing the previous page while a new query is loading', async () => {
    await settle();
    // As the template does: the page is read (rendered) before the query changes.
    expect(store.products()).toEqual([lens, frame]);
    const pending = new Subject<PagedResponse<Product>>();
    api.query.mockReturnValue(pending);

    store.setFilter({ kind: 'name', name: 'frame' });
    TestBed.tick();

    expect(store.isLoading()).toBe(true);
    expect(store.products()).toEqual([lens, frame]);
    expect(store.totalCount()).toBe(2);

    pending.next(aPage([frame]));
    pending.complete();
    await settle();
    expect(store.products()).toEqual([frame]);
  });

  it('bypasses shared caches after the user changes something, but not for background refreshes', async () => {
    await settle();
    api.adjustStock.mockReturnValue(of({ ...lens, stockQuantity: 4 }));

    store.adjustStock(lens, 'remove', 1, 'key').subscribe();
    await settle();

    const tableRequests = api.query.mock.calls.filter(([query]) => query.pageSize !== 1);
    expect(tableRequests.at(-1)?.[1]).toBe(true);
  });

  it('derives the summary cards from the list endpoints', async () => {
    api.query.mockImplementation((query) =>
      of(
        aPage([], {
          totalCount: query.filter.kind === 'all' ? 24 : query.filter.max === 0 ? 1 : 7,
        }),
      ),
    );
    store.reload();
    await settle();

    expect(store.summary()).toEqual({ totalProducts: 24, lowStock: 7, outOfStock: 1 });
  });

  it('exposes load failures as ApiError', async () => {
    api.query.mockReturnValue(throwError(() => new ApiError(0, 'network_error', 'Offline')));
    store.setFilter({ kind: 'stock', min: 0, max: 0 });
    await settle();

    expect(store.error()?.code).toBe('network_error');
  });
});

describe('ProductsStore auto-refresh', () => {
  let query: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers();
    query = vi.fn<(query: ProductQuery) => Observable<PagedResponse<Product>>>(() => of(aPage([])));
    TestBed.configureTestingModule({
      providers: [
        ProductsStore,
        { provide: ProductsApi, useValue: { query } },
        { provide: PRODUCTS_AUTO_REFRESH_MS, useValue: 1_000 },
      ],
    });
    TestBed.inject(ProductsStore);
    TestBed.tick();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  const listRequests = () => query.mock.calls.filter(([q]) => q.pageSize !== 1).length;

  it('re-fetches periodically so changes made by others show up', () => {
    expect(listRequests()).toBe(1);

    vi.advanceTimersByTime(1_000);
    TestBed.tick();
    vi.advanceTimersByTime(1_000);
    TestBed.tick();

    expect(listRequests()).toBe(3);
  });

  it('pauses while the browser tab is hidden', () => {
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');

    vi.advanceTimersByTime(5_000);
    TestBed.tick();

    expect(listRequests()).toBe(1);
  });
});
