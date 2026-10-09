import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot } from '@angular/router';
import { Auth } from './auth';
import { authGuard } from './auth-guard';

describe('authGuard', () => {
  function run(isAuthenticated: boolean) {
    const login = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [{ provide: Auth, useValue: { isAuthenticated: signal(isAuthenticated), login } }],
    });
    const result = TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url: '/products?page=3' } as RouterStateSnapshot),
    );
    return { result, login };
  }

  it('lets signed-in users through', async () => {
    const { result, login } = run(true);

    expect(await result).toBe(true);
    expect(login).not.toHaveBeenCalled();
  });

  it('sends anonymous users to sign in and back to where they were going', async () => {
    const { result, login } = run(false);

    expect(await result).toBe(false);
    expect(login).toHaveBeenCalledWith('/products?page=3');
  });
});
