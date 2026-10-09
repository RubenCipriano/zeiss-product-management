import { signal } from '@angular/core';
import { Auth, Permission } from '../app/core/auth/auth';

/** Stand-in for {@link Auth} in component tests: a signed-in user with the given permissions. */
export function fakeAuth(
  permissions: readonly Permission[] = ['products:read', 'stock:write', 'products:write'],
) {
  const granted = signal(new Set<Permission>(permissions));
  return {
    provide: Auth,
    useValue: {
      isAuthenticated: signal(true),
      userName: signal('test-user'),
      roles: signal(['manager']),
      can: (permission: Permission) => granted().has(permission),
      accessToken: () => 'test-token',
      isTokenRequest: () => false,
      login: vi.fn(),
      logout: vi.fn(),
    },
  };
}
