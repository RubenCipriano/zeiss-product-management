import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  provideRouter,
  RouterStateSnapshot,
  UrlTree,
} from '@angular/router';
import { Auth } from './auth';
import { authGuard } from './auth-guard';

describe('authGuard', () => {
  function run(isAuthenticated: boolean) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: Auth, useValue: { isAuthenticated: signal(isAuthenticated) } },
      ],
    });
    return TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url: '/products?page=3' } as RouterStateSnapshot),
    );
  }

  it('lets signed-in users through', () => {
    expect(run(true)).toBe(true);
  });

  it('sends anonymous users to the login page, remembering where they were going', () => {
    const result = run(false) as UrlTree;

    expect(result.toString()).toBe('/login?returnUrl=%2Fproducts%3Fpage%3D3');
  });
});
