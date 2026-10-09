import {
  computed,
  DestroyRef,
  DOCUMENT,
  effect,
  inject,
  InjectionToken,
  linkedSignal,
  Service,
  signal,
  untracked,
} from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { combineLatest, filter, interval, map, Observable, tap } from 'rxjs';
import { ApiError } from '../../../core/http/api-error';
import { emptyPage, PagedResponse } from '../../../shared/models/paged-response';
import {
  DEFAULT_PAGE_SIZE,
  InventorySummary,
  LOW_STOCK_THRESHOLD,
  Product,
  ProductFilter,
  ProductInput,
  ProductQuery,
  StockOperation,
} from './product-models';
import { ProductsApi } from './products-api';

/**
 * How often the list re-fetches in the background, so changes made by other users or other API
 * clients show up without a manual refresh. 0 disables it.
 */
export const PRODUCTS_AUTO_REFRESH_MS = new InjectionToken<number>('PRODUCTS_AUTO_REFRESH_MS', {
  factory: () => 15_000,
});

/**
 * State of the product list screen: the current query, the loaded page and the mutations that
 * affect it. Provided by the page component, so it lives exactly as long as the screen.
 *
 * Data flow is one-directional: components call methods, the store updates `query`, and
 * `rxResource` re-fetches. Changing the query while a request is in flight cancels it.
 */
@Service({ autoProvided: false })
export class ProductsStore {
  private readonly api = inject(ProductsApi);
  private readonly document = inject(DOCUMENT);

  readonly query = signal<ProductQuery>(
    { filter: { kind: 'all' }, page: 1, pageSize: DEFAULT_PAGE_SIZE },
    // Structural equality: re-applying the same filter must not trigger another request.
    { equal: (a, b) => JSON.stringify(a) === JSON.stringify(b) },
  );

  /** Set before a reload that must bypass shared caches (after the user's own changes or an explicit refresh). */
  private nextLoadFresh = false;
  private summaryFresh = false;

  /**
   * Each result remembers the query it answers. Changing the query cancels the request in flight, so paging quickly
   * never lets an older page overwrite a newer one.
   */
  private readonly productsResource = rxResource({
    params: () => this.query(),
    stream: ({ params }) =>
      this.api.query(params, this.consumeFresh()).pipe(map((page) => ({ query: params, page }))),
  });

  private readonly loaded = linkedSignal<
    { query: ProductQuery; page: PagedResponse<Product> } | undefined,
    { query: ProductQuery | null; page: PagedResponse<Product> }
  >({
    source: () => (this.productsResource.hasValue() ? this.productsResource.value() : undefined),
    computation: (current, previous) =>
      current ?? previous?.value ?? { query: null, page: emptyPage(DEFAULT_PAGE_SIZE) },
  });

  /**
   * The last successfully loaded page. While a new query or a reload is in flight (or after it fails) the
   * previous page stays on screen, so the table and paginator don't flash empty between requests.
   */
  readonly page = computed(() => this.loaded().page);

  /**
   * True while the screen shows a different page or filter than the one requested (the user is paging or filtering),
   * as opposed to a background refresh of the same page. The UI dims the table only in this case.
   */
  readonly isChangingPage = computed(
    () => this.isLoading() && !sameQuery(this.loaded().query, this.query()),
  );

  private lastPrefetch = '';

  /**
   * Counts for the summary cards. They come from the same list endpoints with `pageSize=1` (only `totalCount` is
   * used), so the API needs no dedicated statistics endpoint and the counts are cached like any other read.
   */
  private readonly summaryResource = rxResource({
    stream: () =>
      // combineLatest (not forkJoin): cached counts show at once, refreshed counts replace them.
      combineLatest({
        all: this.api.query({ filter: { kind: 'all' }, page: 1, pageSize: 1 }, this.summaryFresh),
        low: this.api.query(
          { filter: { kind: 'stock', min: 0, max: LOW_STOCK_THRESHOLD }, page: 1, pageSize: 1 },
          this.summaryFresh,
        ),
        out: this.api.query(
          { filter: { kind: 'stock', min: 0, max: 0 }, page: 1, pageSize: 1 },
          this.summaryFresh,
        ),
      }).pipe(
        map(({ all, low, out }): InventorySummary => ({
          totalProducts: all.totalCount,
          lowStock: low.totalCount,
          outOfStock: out.totalCount,
        })),
      ),
  });

  /** Last known summary; `null` until the first load (and kept while reloading). */
  readonly summary = linkedSignal<InventorySummary | undefined, InventorySummary | null>({
    source: () => (this.summaryResource.hasValue() ? this.summaryResource.value() : undefined),
    computation: (current, previous) => current ?? previous?.value ?? null,
  }).asReadonly();

  readonly products = computed(() => this.page().items);
  readonly totalCount = computed(() => this.page().totalCount);
  readonly isLoading = this.productsResource.isLoading;
  readonly error = computed(() => toApiError(this.productsResource.error()));

  constructor() {
    const refreshMs = inject(PRODUCTS_AUTO_REFRESH_MS);
    if (refreshMs > 0) {
      interval(refreshMs)
        .pipe(
          filter(() => this.document.visibilityState === 'visible' && !this.isLoading()),
          takeUntilDestroyed(inject(DestroyRef)),
        )
        // Background refreshes may be served by the gateway's microcache.
        .subscribe(() => this.reload({ fresh: false }));
    }

    // Prefetch the next page once a page has loaded, so "next" is usually instant (served by the browser cache).
    effect(() => {
      const { query, page } = this.loaded();
      if (!query || query.page * query.pageSize >= page.totalCount) {
        return;
      }
      const next = { ...query, page: query.page + 1 };
      const key = JSON.stringify(next);
      if (key !== this.lastPrefetch) {
        this.lastPrefetch = key;
        untracked(() => this.api.query(next).subscribe({ error: () => undefined }));
      }
    });
  }

  setFilter(filter: ProductFilter): void {
    this.query.update((query) => ({ ...query, filter, page: 1 }));
  }

  setPage(page: number, pageSize: number): void {
    this.query.update((query) => ({ ...query, page, pageSize }));
  }

  /** `fresh` (default) bypasses shared caches: used for explicit refreshes and after the user's own changes. */
  reload({ fresh } = { fresh: true }): void {
    this.nextLoadFresh = fresh;
    this.summaryFresh = fresh;
    this.productsResource.reload();
    this.summaryResource.reload();
  }

  create(input: ProductInput, idempotencyKey: string): Observable<Product> {
    return this.api.create(input, idempotencyKey).pipe(tap(() => this.reload()));
  }

  update(product: Product, input: ProductInput): Observable<Product> {
    return this.api.update(product, input).pipe(tap((updated) => this.applyChange(updated)));
  }

  delete(product: Product): Observable<void> {
    return this.api.delete(product).pipe(tap(() => this.reload()));
  }

  adjustStock(
    product: Product,
    operation: StockOperation,
    quantity: number,
    idempotencyKey: string,
  ): Observable<Product> {
    return this.api
      .adjustStock(product, operation, quantity, idempotencyKey)
      .pipe(tap((updated) => this.applyChange(updated)));
  }

  private consumeFresh(): boolean {
    const fresh = this.nextLoadFresh;
    this.nextLoadFresh = false;
    return fresh;
  }

  /**
   * Shows the server's answer immediately, then re-fetches: the change may move the product to
   * another page or out of the current filter (e.g. a stock-level range).
   */
  private applyChange(updated: Product): void {
    if (this.productsResource.hasValue()) {
      this.productsResource.value.update((loaded) => ({
        ...loaded,
        page: {
          ...loaded.page,
          items: loaded.page.items.map((item) => (item.id === updated.id ? updated : item)),
        },
      }));
    }
    this.reload();
  }
}

function sameQuery(a: ProductQuery | null, b: ProductQuery): boolean {
  return a !== null && JSON.stringify(a) === JSON.stringify(b);
}

function toApiError(error: unknown): ApiError | null {
  if (!error) {
    return null;
  }
  return error instanceof ApiError
    ? error
    : new ApiError(-1, 'unexpected_error', 'Something unexpected went wrong.');
}
