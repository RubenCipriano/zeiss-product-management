import { HttpClient, HttpContext, HttpHeaders, HttpParams } from '@angular/common/http';
import { inject, Service } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../../core/config/api-config';
import { CLIENT_CACHE, LIST_CACHE_POLICY } from '../../../core/http/client-cache-interceptor';
import { PagedResponse } from '../../../shared/models/paged-response';
import {
  Product,
  ProductFilter,
  ProductInput,
  ProductQuery,
  StockMovement,
  StockOperation,
} from './product-models';

/**
 * The products endpoints of the API as typed methods: one method per HTTP call, no state. A failed call arrives as an
 * `ApiError` (see apiErrorInterceptor).
 */
@Service()
export class ProductsApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${inject(API_BASE_URL)}/products`;

  /**
   * One page of products. The API has a separate list endpoint per kind of filter (all, by name, by stock level).
   * @param fresh skip the caches (browser and gateway): used right after the user's own changes so they always see the
   * result.
   */
  query(
    { filter, page, pageSize }: ProductQuery,
    fresh = false,
  ): Observable<PagedResponse<Product>> {
    const paging = new HttpParams().set('page', page).set('pageSize', pageSize);
    const { url, params } = this.listEndpoint(filter, paging);
    return this.http.get<PagedResponse<Product>>(url, {
      params,
      headers: noCacheIf(fresh),
      context: cached(),
    });
  }

  /** @param fresh skip shared caches, as for {@link query}. */
  getById(id: number, fresh = false): Observable<Product> {
    return this.http.get<Product>(`${this.baseUrl}/${id}`, { headers: noCacheIf(fresh) });
  }

  /**
   * @param idempotencyKey sent as the `Idempotency-Key` header, one key per user action: if the same request arrives
   * twice (double click, network error, a retry reaching another server), the API applies it only once.
   */
  create(input: ProductInput, idempotencyKey: string): Observable<Product> {
    return this.http.post<Product>(this.baseUrl, input, { headers: idempotent(idempotencyKey) });
  }

  /**
   * Sends the version the user edited in the `If-Match` header: if someone else changed the product meanwhile, the API
   * answers 412 (Precondition Failed) instead of overwriting their change.
   */
  update(product: Pick<Product, 'id' | 'version'>, input: ProductInput): Observable<Product> {
    return this.http.put<Product>(`${this.baseUrl}/${product.id}`, input, {
      headers: ifMatch(product.version),
    });
  }

  delete(product: Pick<Product, 'id' | 'version'>): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${product.id}`, {
      headers: ifMatch(product.version),
    });
  }

  adjustStock(
    product: Pick<Product, 'id'>,
    operation: StockOperation,
    quantity: number,
    idempotencyKey: string,
  ): Observable<Product> {
    const action = operation === 'remove' ? 'decrement-stock' : 'add-to-stock';
    return this.http.post<Product>(`${this.baseUrl}/${product.id}/${action}/${quantity}`, null, {
      headers: idempotent(idempotencyKey),
    });
  }

  stockMovements(
    productId: number,
    page: number,
    pageSize: number,
  ): Observable<PagedResponse<StockMovement>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResponse<StockMovement>>(
      `${this.baseUrl}/${productId}/stock-movements`,
      { params, context: cached() },
    );
  }

  /** The list endpoint for a filter, and its query parameters added to `params`. */
  private listEndpoint(
    filter: ProductFilter,
    params: HttpParams,
  ): { url: string; params: HttpParams } {
    switch (filter.kind) {
      case 'all':
        return { url: this.baseUrl, params };
      case 'name':
        // An empty search is no filter at all.
        return filter.name.trim()
          ? { url: `${this.baseUrl}/search`, params: params.set('name', filter.name) }
          : { url: this.baseUrl, params };
      case 'stock': {
        let stockParams = params;
        if (filter.min !== null) {
          stockParams = stockParams.set('min', filter.min);
        }
        if (filter.max !== null) {
          stockParams = stockParams.set('max', filter.max);
        }
        return { url: `${this.baseUrl}/stock-level`, params: stockParams };
      }
    }
  }
}

/**
 * `Cache-Control: no-cache` when `fresh`: the browser cache and the gateway's microcache then skip their stored copy, ask
 * the API, and keep its new answer.
 */
function noCacheIf(fresh: boolean): HttpHeaders | undefined {
  return fresh ? new HttpHeaders({ 'Cache-Control': 'no-cache' }) : undefined;
}

/** Marks a read as cacheable in the browser (see clientCacheInterceptor); writes empty that cache. */
function cached(): HttpContext {
  return new HttpContext().set(CLIENT_CACHE, LIST_CACHE_POLICY);
}

function idempotent(key: string): HttpHeaders {
  return new HttpHeaders({ 'Idempotency-Key': key });
}

function ifMatch(version: number): HttpHeaders {
  return new HttpHeaders({ 'If-Match': `"${version}"` });
}
