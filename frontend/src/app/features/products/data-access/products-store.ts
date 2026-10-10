// The products page's data: which page and filter are shown, the loaded products, the summary counts, and the changes
// the user makes. Components read it and call its methods; they never call the API themselves.
import {
  computed,
  effect,
  inject,
  InjectionToken,
  linkedSignal,
  Service,
  signal,
  untracked,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { combineLatest, map, Observable, tap } from 'rxjs';
import { ApiError } from '../../../core/http/api-error';
import { emptyPage, PagedResponse } from '../../../shared/models/paged-response';
import { autoRefresh } from '../../../shared/util';
import {
  DEFAULT_PAGE_SIZE,
  InventorySummary,
  LOW_STOCK_FILTER,
  OUT_OF_STOCK_FILTER,
  Product,
  ProductFilter,
  ProductInput,
  ProductQuery,
  StockOperation,
} from './product-models';
import { ProductsApi } from './products-api';

/**
 * How often the list re-fetches in the background, so changes made by other users or other API
 * clients show up without a manual refresh. 0 disables it (an InjectionToken, so tests can).
 */
export const PRODUCTS_AUTO_REFRESH_MS = new InjectionToken<number>('PRODUCTS_AUTO_REFRESH_MS', {
  factory: () => 15_000,
});

/** A page of products together with the query it answers. */
interface LoadedPage {
  query: ProductQuery;
  page: PagedResponse<Product>;
}

/**
 * State of the products page, kept in signals: values that templates and `computed` values re-read automatically when
 * they change. Provided by the page component (`autoProvided: false`), so it lives exactly as long as the page.
 *
 * Data flows one way: a component calls a method (`setFilter`, `setPage`, `update`...), the store changes `query`, and
 * `rxResource` (a signal-driven loader: it re-runs its request whenever its `params` change, cancelling the one in
 * flight) fetches the new page. Background: docs/adr/0007-frontend-state.md, docs/guide/07-frontend.md.
 */
@Service({ autoProvided: false })
export class ProductsStore {
  private readonly api = inject(ProductsApi);

  /** What the user asked to see. Every change triggers a load (see productsResource). */
  readonly query = signal<ProductQuery>(
    { filter: { kind: 'all' }, page: 1, pageSize: DEFAULT_PAGE_SIZE },
    // Structural equality: re-applying the same filter must not trigger another request.
    { equal: (a, b) => JSON.stringify(a) === JSON.stringify(b) },
  );

  /** Set before a reload that must bypass the caches (after the user's own changes or an explicit refresh). */
  private nextLoadFresh = false;
  private summaryFresh = false;
  /** The next page last prefetched, so the same one is not fetched twice. */
  private lastPrefetch = '';

  /**
   * Loads the page for `query`. Each result remembers the query it answers. Changing the query cancels the request in
   * flight, so paging quickly never lets an older page overwrite a newer one.
   */
  private readonly productsResource = rxResource({
    params: () => this.query(),
    stream: ({ params }) =>
      this.api
        .query(params, this.consumeFresh())
        .pipe(map((page): LoadedPage => ({ query: params, page }))),
  });

  /**
   * The last page that loaded successfully. A linkedSignal is a signal computed from a source that also sees its own
   * previous value: while a new load is in flight (or after it fails) it keeps the previous page.
   */
  private readonly loaded = linkedSignal<
    LoadedPage | undefined,
    { query: ProductQuery | null; page: PagedResponse<Product> }
  >({
    source: () => (this.productsResource.hasValue() ? this.productsResource.value() : undefined),
    computation: (current, previous) =>
      current ?? previous?.value ?? { query: null, page: emptyPage(DEFAULT_PAGE_SIZE) },
  });

  /**
   * Counts for the summary cards. They come from the same list endpoints with `pageSize=1` (only `totalCount` is
   * used), so the API needs no dedicated statistics endpoint and the counts are cached like any other read.
   */
  private readonly summaryResource = rxResource({
    stream: () =>
      // combineLatest (not forkJoin): cached counts show at once, refreshed counts replace them.
      combineLatest({
        totalProducts: this.count({ kind: 'all' }),
        lowStock: this.count(LOW_STOCK_FILTER),
        outOfStock: this.count(OUT_OF_STOCK_FILTER),
      }),
  });

  /**
   * The page on screen: the last one loaded. While a new query or a reload is in flight (or after it fails) the
   * previous page stays, so the table and paginator don't flash empty between requests.
   */
  readonly page = computed(() => this.loaded().page);
  readonly products = computed(() => this.page().items);
  readonly totalCount = computed(() => this.page().totalCount);
  readonly isLoading = this.productsResource.isLoading;
  readonly error = computed(() => toApiError(this.productsResource.error()));

  /**
   * True while the screen shows a different page or filter than the one requested (the user is paging or filtering),
   * as opposed to a background refresh of the same page. The UI dims the table only in this case.
   */
  readonly isChangingPage = computed(
    () => this.isLoading() && !sameQuery(this.loaded().query, this.query()),
  );

  /** Last known summary; `null` until the first load (and kept while reloading). */
  readonly summary = linkedSignal<InventorySummary | undefined, InventorySummary | null>({
    source: () => (this.summaryResource.hasValue() ? this.summaryResource.value() : undefined),
    computation: (current, previous) => current ?? previous?.value ?? null,
  }).asReadonly();

  constructor() {
    // Background refreshes may be served by the caches (browser, gateway): no `fresh`.
    autoRefresh(() => this.reload({ fresh: false }), {
      everyMs: inject(PRODUCTS_AUTO_REFRESH_MS),
      when: () => !this.isLoading(),
    });

    // An effect runs its function now and again whenever a signal it read changes: here, whenever a page loads.
    effect(() => this.prefetchNextPage());
  }

  setFilter(filter: ProductFilter): void {
    this.query.update((query) => ({ ...query, filter, page: 1 }));
  }

  setPage(page: number, pageSize: number): void {
    this.query.update((query) => ({ ...query, page, pageSize }));
  }

  /** `fresh` (default) bypasses the caches: used for explicit refreshes and after the user's own changes. */
  reload({ fresh } = { fresh: true }): void {
    this.nextLoadFresh = fresh;
    this.summaryFresh = fresh;
    this.productsResource.reload();
    this.summaryResource.reload();
  }

  /**
   * The product as the server has it now, bypassing the caches: after a 412 the user re-applies their edit on top of
   * it, so it must not be the version they already saw.
   */
  latest(id: number): Observable<Product> {
    return this.api.getById(id, true);
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

  /** How many products match `filter`: one product per page is enough to get `totalCount`. */
  private count(filter: ProductFilter): Observable<number> {
    return this.api
      .query({ filter, page: 1, pageSize: 1 }, this.summaryFresh)
      .pipe(map((page) => page.totalCount));
  }

  private consumeFresh(): boolean {
    const fresh = this.nextLoadFresh;
    this.nextLoadFresh = false;
    return fresh;
  }

  /** Once a page has loaded, fetches the next one, so "next" is usually instant (served by the browser cache). */
  private prefetchNextPage(): void {
    const { query, page } = this.loaded();
    const hasNextPage = query !== null && query.page * query.pageSize < page.totalCount;
    if (!hasNextPage) {
      return;
    }
    const next = { ...query, page: query.page + 1 };
    const key = JSON.stringify(next);
    if (key !== this.lastPrefetch) {
      this.lastPrefetch = key;
      untracked(() => this.api.query(next).subscribe({ error: () => undefined }));
    }
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
