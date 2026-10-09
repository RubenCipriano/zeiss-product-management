import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { Auth, SESSION_STORAGE } from './auth';

/** An unsigned JWT with the given payload (the client only reads claims; the API verifies signatures). */
function jwt(payload: object): string {
  const encode = (value: object) =>
    btoa(JSON.stringify(value)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}.signature`;
}

describe('Auth', () => {
  let storage: Storage;
  let httpTesting: HttpTestingController;

  function setup(): Auth {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SESSION_STORAGE, useValue: storage },
      ],
    });
    httpTesting = TestBed.inject(HttpTestingController);
    return TestBed.inject(Auth);
  }

  async function signIn(auth: Auth, roles: string | string[], expiresIn = 3600): Promise<void> {
    const done = firstValueFrom(auth.login('maria', 'secret'));
    httpTesting
      .expectOne({ method: 'POST', url: '/api/auth/token' })
      .flush({ accessToken: jwt({ sub: 'maria', roles }), tokenType: 'Bearer', expiresIn });
    await done;
  }

  beforeEach(() => {
    storage = window.sessionStorage;
    storage.clear();
  });

  it('exchanges the credentials for a token at the API', async () => {
    const auth = setup();
    const done = firstValueFrom(auth.login('maria', 's3cret'));

    const request = httpTesting.expectOne({ method: 'POST', url: '/api/auth/token' });
    expect(request.request.body).toEqual({ clientId: 'maria', clientSecret: 's3cret' });
    request.flush({
      accessToken: jwt({ sub: 'maria', roles: ['viewer'] }),
      tokenType: 'Bearer',
      expiresIn: 3600,
    });
    await done;

    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.userName()).toBe('maria');
    expect(auth.accessToken()).toContain('.');
  });

  it.each([
    ['viewer', { read: true, stock: false, write: false }],
    ['stock-clerk', { read: true, stock: true, write: false }],
    ['manager', { read: true, stock: true, write: true }],
  ])('maps the %s role to the same permissions as the API', async (role, expected) => {
    const auth = setup();
    await signIn(auth, role);

    expect({
      read: auth.can('products:read'),
      stock: auth.can('stock:write'),
      write: auth.can('products:write'),
    }).toEqual(expected);
  });

  it('keeps the session across a page reload until it expires', async () => {
    await signIn(setup(), ['manager']);

    TestBed.resetTestingModule();
    const reloaded = setup();

    expect(reloaded.isAuthenticated()).toBe(true);
    expect(reloaded.can('products:write')).toBe(true);
  });

  it('signs out automatically when the token expires', async () => {
    vi.useFakeTimers();
    try {
      const auth = setup();
      await signIn(auth, 'viewer', 60);

      vi.advanceTimersByTime(60_000);

      expect(auth.isAuthenticated()).toBe(false);
      expect(auth.accessToken()).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it('forgets the session on logout', async () => {
    const auth = setup();
    await signIn(auth, 'viewer');

    auth.logout();
    TestBed.resetTestingModule();

    expect(setup().isAuthenticated()).toBe(false);
  });
});
