import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiErrorInterceptor } from '../http/api-error-interceptor';
import { Auth } from './auth';
import { authInterceptor } from './auth-interceptor';

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let login: ReturnType<typeof vi.fn>;

  function setup(token: string | null) {
    login = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor, apiErrorInterceptor])),
        provideHttpClientTesting(),
        { provide: Auth, useValue: { accessToken: () => token, login } },
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
  }

  afterEach(() => httpTesting.verify());

  it('adds the bearer token to API calls', () => {
    setup('abc');

    http.get('/api/products').subscribe();

    expect(httpTesting.expectOne('/api/products').request.headers.get('Authorization')).toBe(
      'Bearer abc',
    );
  });

  it('never sends the token to other origins', () => {
    setup('abc');

    http.get('https://cdn.example.com/data.json').subscribe();
    http.get('/apiary').subscribe();

    expect(
      httpTesting
        .expectOne('https://cdn.example.com/data.json')
        .request.headers.has('Authorization'),
    ).toBe(false);
    expect(httpTesting.expectOne('/apiary').request.headers.has('Authorization')).toBe(false);
  });

  it('sends the user to sign in again when the API answers 401', () => {
    setup('expired');

    http.get('/api/products').subscribe({ error: () => undefined });
    httpTesting
      .expectOne('/api/products')
      .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });

    expect(login).toHaveBeenCalled();
  });

  it('does not ask for sign-in on other errors', () => {
    setup('abc');

    http.get('/api/products').subscribe({ error: () => undefined });
    httpTesting
      .expectOne('/api/products')
      .flush({ title: 'Forbidden', status: 403 }, { status: 403, statusText: 'Forbidden' });

    expect(login).not.toHaveBeenCalled();
  });
});
