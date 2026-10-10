import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Auth } from './auth';

/**
 * Route guard for pages that need a signed-in user. A guard is a function the router calls before opening a page:
 * `true` lets the user in; a URL tree redirects instead, here to the login page, which comes back afterwards
 * (`returnUrl`).
 */
export const authGuard: CanActivateFn = (_route, state) =>
  inject(Auth).isAuthenticated()
    ? true
    : inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
