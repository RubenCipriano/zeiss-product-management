import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { inject, Service } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../../core/config/api-config';
import { PagedResponse } from '../../../shared/models/paged-response';
import {
  Product,
  ProductInput,
  ProductQuery,
  StockMovement,
  StockOperation,
} from './product-models';

/**
 * Typed client for the products endpoints. Stateless: it only maps calls to HTTP. Errors arrive as
 * `ApiError` (see apiErrorInterceptor).
 */
@Service()
export class ProductsApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${inject(API_BASE_URL)}/products`;

  /**
   * @param fresh skip shared caches (the gateway's 1 s microcache) - used right after the user's own changes so they
   * always see the result.
   */
  query(
    { filter, page, pageSize }: ProductQuery,
    fresh = false,
  ): Observable<PagedResponse<Product>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    const headers = fresh ? new HttpHeaders({ 'Cache-Control': 'no-cache' }) : undefined;

    switch (filter.kind) {
      case 'all':
        return this.http.get<PagedResponse<Product>>(this.baseUrl, { params, headers });
      case 'name':
        params = params.set('name', filter.name);
        return this.http.get<PagedResponse<Product>>(`${this.baseUrl}/search`, { params, headers });
      case 'stock':
        if (filter.min !== null) {
          params = params.set('min', filter.min);
        }
        if (filter.max !== null) {
          params = params.set('max', filter.max);
        }
        return this.http.get<PagedResponse<Product>>(`${this.baseUrl}/stock-level`, {
          params,
          headers,
        });
    }
  }

  getById(id: number): Observable<Product> {
    return this.http.get<Product>(`${this.baseUrl}/${id}`);
  }

  /**
   * @param idempotencyKey one key per user action: if the request is retried (double click, network error, a retry
   * reaching another server), the API applies it only once.
   */
  create(input: ProductInput, idempotencyKey: string): Observable<Product> {
    return this.http.post<Product>(this.baseUrl, input, { headers: idempotent(idempotencyKey) });
  }

  /** Sends the version the user edited as `If-Match`: if someone else changed it meanwhile, the API answers 412. */
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
      { params },
    );
  }
}

function idempotent(key: string): HttpHeaders {
  return new HttpHeaders({ 'Idempotency-Key': key });
}

function ifMatch(version: number): HttpHeaders {
  return new HttpHeaders({ 'If-Match': `"${version}"` });
}
