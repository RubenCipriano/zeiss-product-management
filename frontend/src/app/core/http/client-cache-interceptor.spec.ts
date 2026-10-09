import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  CLIENT_CACHE,
  clientCacheInterceptor,
  ClientCachePolicy,
} from './client-cache-interceptor';
import { ResponseCache } from './response-cache';

describe('clientCacheInterceptor', () => {
  const policy: ClientCachePolicy = { freshMs: 5_000, staleMs: 60_000 };
  const cached = { context: new HttpContext().set(CLIENT_CACHE, policy) };
  let http: HttpClient;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([clientCacheInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTesting.verify();
    vi.useRealTimers();
  });

  function get(url = '/api/products?page=2', options = cached): unknown[] {
    const values: unknown[] = [];
    http.get(url, options).subscribe((value) => values.push(value));
    return values;
  }

  it('answers a fresh repeat from memory, without a request', () => {
    get();
    httpTesting.expectOne('/api/products?page=2').flush({ page: 2 });

    const values = get();

    expect(values).toEqual([{ page: 2 }]);
    httpTesting.expectNone('/api/products?page=2');
  });

  it('shows a stale answer at once and replaces it with the API response', () => {
    get();
    httpTesting.expectOne('/api/products?page=2').flush({ version: 1 });
    vi.advanceTimersByTime(10_000);

    const values = get();
    httpTesting.expectOne('/api/products?page=2').flush({ version: 2 });

    expect(values).toEqual([{ version: 1 }, { version: 2 }]);
  });

  it('skips the cache for requests that ask for fresh data, and stores their answer', () => {
    get();
    httpTesting.expectOne('/api/products?page=2').flush({ version: 1 });

    const fresh = { ...cached, headers: { 'Cache-Control': 'no-cache' } };
    const values = get('/api/products?page=2', fresh as typeof cached);
    httpTesting.expectOne('/api/products?page=2').flush({ version: 2 });

    expect(values).toEqual([{ version: 2 }]);
    expect(get()).toEqual([{ version: 2 }]);
  });

  it('forgets everything after a change', () => {
    get();
    httpTesting.expectOne('/api/products?page=2').flush({ version: 1 });

    http.post('/api/products/1/add-to-stock/1', null).subscribe();
    httpTesting.expectOne('/api/products/1/add-to-stock/1').flush({});

    expect(TestBed.inject(ResponseCache).size).toBe(0);
  });

  it('only caches requests that opt in', () => {
    get('/api/other', {} as typeof cached);
    httpTesting.expectOne('/api/other').flush({});

    get('/api/other', {} as typeof cached);
    httpTesting.expectOne('/api/other').flush({});
  });
});
