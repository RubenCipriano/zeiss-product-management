import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth-guard';

/**
 * The app's URLs and the page each one shows. Pages are lazy-loaded: `loadComponent` uses a dynamic `import()`, so each
 * page's code is a separate file the browser downloads only on the first visit. `canActivate: [authGuard]` sends a
 * signed-out user to the login page first.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'products' },
  {
    path: 'login',
    title: 'Sign in · Product Management',
    loadComponent: () => import('./features/auth/login/login').then((m) => m.Login),
  },
  {
    path: 'products',
    title: 'Products · Product Management',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/products/feature/products-page/products-page').then((m) => m.ProductsPage),
  },
  { path: '**', redirectTo: 'products' },
];
