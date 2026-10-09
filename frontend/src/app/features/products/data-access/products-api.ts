import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { inject, Service } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../../../core/config/api-config';
import { PagedResponse } from '../../../shared/models/paged-response';
import { Product, ProductInput, ProductQuery, StockOperation } from './product-models';

/**
 * Typed client for the products endpoints. Stateless: it only maps calls to HTTP. Errors arrive as
 * `ApiError` (see apiErrorInterceptor).
 */
@Service()
export class ProductsApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${inject(API_BASE_URL)}/products`;

  query({ filter, page, pageSize }: ProductQuery): Observable<PagedResponse<Product>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);

    switch (filter.kind) {
      case 'all':
        return this.http.get<PagedResponse<Product>>(this.baseUrl, { params });
      case 'name':
        params = params.set('name', filter.name);
        return this.http.get<PagedResponse<Product>>(`${this.baseUrl}/search`, { params });
      case 'stock':
        if (filter.min !== null) {
          params = params.set('min', filter.min);
        }
        if (filter.max !== null) {
          params = params.set('max', filter.max);
        }
        return this.http.get<PagedResponse<Product>>(`${this.baseUrl}/stock-level`, { params });
    }
  }

  getById(id: number): Observable<Product> {
    return this.http.get<Product>(`${this.baseUrl}/${id}`);
  }

  create(input: ProductInput): Observable<Product> {
    return this.http.post<Product>(this.baseUrl, input);
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
  ): Observable<Product> {
    const action = operation === 'remove' ? 'decrement-stock' : 'add-to-stock';
    return this.http.post<Product>(`${this.baseUrl}/${product.id}/${action}/${quantity}`, null);
  }
}

function ifMatch(version: number): HttpHeaders {
  return new HttpHeaders({ 'If-Match': `"${version}"` });
}
