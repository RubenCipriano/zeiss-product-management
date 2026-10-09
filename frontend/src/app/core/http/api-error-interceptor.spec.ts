import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { ApiError } from './api-error';
import { apiErrorInterceptor } from './api-error-interceptor';

describe('apiErrorInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTesting.verify());

  async function failWith(
    body: object | string,
    status: number,
    statusText = 'Error',
  ): Promise<ApiError> {
    const request = firstValueFrom(http.get('/api/products/1'));
    httpTesting.expectOne('/api/products/1').flush(body, { status, statusText });
    return request.then(
      () => {
        throw new Error('Expected the request to fail');
      },
      (error: ApiError) => error,
    );
  }

  it('maps problem details to an ApiError with code, message and metadata', async () => {
    const error = await failWith(
      {
        title: 'Conflict',
        status: 409,
        detail: 'Cannot remove 5 unit(s) from product 1: only 2 available.',
        errorCode: 'product.insufficient_stock',
        available: 2,
      },
      409,
    );

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(409);
    expect(error.code).toBe('product.insufficient_stock');
    expect(error.message).toBe('Cannot remove 5 unit(s) from product 1: only 2 available.');
    expect(error.problem?.['available']).toBe(2);
  });

  it('exposes validation errors per field', async () => {
    const error = await failWith(
      {
        title: 'One or more validation errors occurred.',
        status: 400,
        errorCode: 'validation_failed',
        errors: { name: ["'Name' must not be empty."], price: ['too many decimals'] },
      },
      400,
    );

    expect(error.fieldError('name')).toBe("'Name' must not be empty.");
    expect(error.fieldError('price')).toBe('too many decimals');
    expect(error.fieldError('categoryId')).toBeNull();
  });

  it('falls back to a generic message when the body is not problem details', async () => {
    const error = await failWith('<html>Bad gateway</html>', 502, 'Bad Gateway');

    expect(error.code).toBe('http_502');
    expect(error.message).toContain('server');
  });

  it('reports network failures distinctly', async () => {
    const request = firstValueFrom(http.get('/api/products/1'));
    httpTesting.expectOne('/api/products/1').error(new ProgressEvent('error'), { status: 0 });

    const error = await request.then(
      () => {
        throw new Error('Expected the request to fail');
      },
      (e: ApiError) => e,
    );

    expect(error.status).toBe(0);
    expect(error.code).toBe('network_error');
  });
});
