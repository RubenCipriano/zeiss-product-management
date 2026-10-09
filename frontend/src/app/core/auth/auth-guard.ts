import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Auth } from './auth';

/** Routes behind this guard require a signed-in user; others are sent to the login page and back afterwards. */
export const authGuard: CanActivateFn = (_route, state) =>
  inject(Auth).isAuthenticated()
    ? true
    : inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
