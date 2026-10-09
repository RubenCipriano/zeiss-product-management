import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { Auth } from './auth';

/** Routes behind this guard require a signed-in user; others are sent to the identity provider first. */
export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(Auth);
  if (auth.isAuthenticated()) {
    return true;
  }

  await auth.login(state.url);
  return false;
};
