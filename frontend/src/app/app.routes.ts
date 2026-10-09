import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth-guard';

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
    // Lazy-loaded: the feature is only downloaded when visited.
    loadComponent: () =>
      import('./features/products/feature/products-page/products-page').then((m) => m.ProductsPage),
  },
  { path: '**', redirectTo: 'products' },
];
