import { TestBed } from '@angular/core/testing';
import { User, UserManager } from 'oidc-client-ts';
import { Auth, USER_MANAGER } from './auth';

describe('Auth', () => {
  function userWithRoles(roles: string[], expired = false): User {
    return {
      access_token: 'token-123',
      expired,
      profile: { sub: '1', preferred_username: 'maria', roles },
      state: undefined,
    } as unknown as User;
  }

  function setup(storedUser: User | null) {
    const userManager = {
      getUser: vi.fn().mockResolvedValue(storedUser),
      signinRedirect: vi.fn().mockResolvedValue(undefined),
      signinRedirectCallback: vi.fn(),
      signoutRedirect: vi.fn(),
      events: {
        addUserLoaded: vi.fn(),
        addUserUnloaded: vi.fn(),
        addAccessTokenExpired: vi.fn(),
      },
    };
    TestBed.configureTestingModule({
      providers: [{ provide: USER_MANAGER, useValue: userManager as unknown as UserManager }],
    });
    return { auth: TestBed.inject(Auth), userManager };
  }

  it.each([
    ['viewer', { read: true, stock: false, write: false }],
    ['stock-clerk', { read: true, stock: true, write: false }],
    ['manager', { read: true, stock: true, write: true }],
  ])('maps the %s role to the same permissions as the API', async (role, expected) => {
    const { auth } = setup(userWithRoles([role]));
    await auth.initialize();

    expect({
      read: auth.can('products:read'),
      stock: auth.can('stock:write'),
      write: auth.can('products:write'),
    }).toEqual(expected);
  });

  it('exposes the user name and the access token of a valid session', async () => {
    const { auth } = setup(userWithRoles(['viewer']));
    await auth.initialize();

    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.userName()).toBe('maria');
    expect(auth.accessToken()).toBe('token-123');
  });

  it('treats an expired session as signed out', async () => {
    const { auth } = setup(userWithRoles(['manager'], true));
    await auth.initialize();

    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.accessToken()).toBeNull();
    expect(auth.can('products:read')).toBe(true); // roles are known, but the guard sends the user to sign in
  });

  it('ignores unknown roles', async () => {
    const { auth } = setup(userWithRoles(['admin', 'offline_access']));
    await auth.initialize();

    expect(auth.can('products:read')).toBe(false);
  });

  it('passes the return URL through the sign-in redirect', async () => {
    const { auth, userManager } = setup(null);

    await auth.login('/products?page=2');

    expect(userManager.signinRedirect).toHaveBeenCalledWith({ state: '/products?page=2' });
  });
});
