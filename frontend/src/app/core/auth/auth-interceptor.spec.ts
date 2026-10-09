import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { apiErrorInterceptor } from '../http/api-error-interceptor';
import { Auth } from './auth';
import { authInterceptor } from './auth-interceptor';

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpTesting: HttpTestingController;
  let logout: ReturnType<typeof vi.fn>;
  let navigate: ReturnType<typeof vi.spyOn>;

  function setup(token: string | null) {
    logout = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor, apiErrorInterceptor])),
        provideHttpClientTesting(),
        {
          provide: Auth,
          useValue: {
            accessToken: () => token,
            logout,
            isTokenRequest: (url: string) => url === '/api/auth/token',
          },
        },
      ],
    });
    http = TestBed.inject(HttpClient);
    httpTesting = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
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

  it('ends the session and sends the user to sign in when the API answers 401', () => {
    setup('expired');

    http.get('/api/products').subscribe({ error: () => undefined });
    httpTesting
      .expectOne('/api/products')
      .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });

    expect(logout).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/login'], expect.anything());
  });

  it('leaves failed sign-ins to the login form', () => {
    setup(null);

    http.post('/api/auth/token', {}).subscribe({ error: () => undefined });
    httpTesting
      .expectOne('/api/auth/token')
      .flush({ title: 'Unauthorized', status: 401 }, { status: 401, statusText: 'Unauthorized' });

    expect(logout).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
  });
});
