import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ProductsApi } from './products-api';

describe('ProductsApi', () => {
  let api: ProductsApi;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ProductsApi);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  it('lists all products with paging parameters', () => {
    api.query({ filter: { kind: 'all' }, page: 2, pageSize: 50 }).subscribe();

    const request = httpTesting.expectOne((req) => req.url === '/api/products');
    expect(request.request.params.get('page')).toBe('2');
    expect(request.request.params.get('pageSize')).toBe('50');
  });

  it('asks shared caches to revalidate when fresh data is required', () => {
    api.query({ filter: { kind: 'all' }, page: 1, pageSize: 20 }, true).subscribe();
    api.query({ filter: { kind: 'all' }, page: 2, pageSize: 20 }).subscribe();

    const [fresh, normal] = httpTesting.match((req) => req.url === '/api/products');
    expect(fresh.request.headers.get('Cache-Control')).toBe('no-cache');
    expect(normal.request.headers.has('Cache-Control')).toBe(false);
  });

  it('uses the search endpoint for name filters', () => {
    api.query({ filter: { kind: 'name', name: 'lens' }, page: 1, pageSize: 20 }).subscribe();

    const request = httpTesting.expectOne((req) => req.url === '/api/products/search');
    expect(request.request.params.get('name')).toBe('lens');
  });

  it('uses the stock-level endpoint and omits open bounds', () => {
    api.query({ filter: { kind: 'stock', min: 0, max: null }, page: 1, pageSize: 20 }).subscribe();

    const request = httpTesting.expectOne((req) => req.url === '/api/products/stock-level');
    expect(request.request.params.get('min')).toBe('0');
    expect(request.request.params.has('max')).toBe(false);
  });

  it('sends the edited version as If-Match on update and delete', () => {
    const product = { id: 123456, version: 42 };
    const input = { name: 'Frame', description: null, price: 10, stockQuantity: 1, categoryId: 2 };

    api.update(product, input).subscribe();
    api.delete(product).subscribe();

    const update = httpTesting.expectOne({ method: 'PUT', url: '/api/products/123456' });
    const remove = httpTesting.expectOne({ method: 'DELETE', url: '/api/products/123456' });
    expect(update.request.headers.get('If-Match')).toBe('"42"');
    expect(update.request.body).toEqual(input);
    expect(remove.request.headers.get('If-Match')).toBe('"42"');
  });

  it.each([
    ['remove', '/api/products/123456/decrement-stock/3'],
    ['add', '/api/products/123456/add-to-stock/3'],
  ] as const)('maps the %s stock operation to its endpoint', (operation, url) => {
    api.adjustStock({ id: 123456 }, operation, 3).subscribe();

    httpTesting.expectOne({ method: 'POST', url });
  });
});
