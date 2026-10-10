import { HttpClient } from '@angular/common/http';
import { inject, Service } from '@angular/core';
import { Observable, shareReplay } from 'rxjs';
import { API_BASE_URL } from '../../../core/config/api-config';
import { Category } from './product-models';

/**
 * Loads the product categories (for the form's dropdown). They never change while the app runs, so they are fetched
 * once: `shareReplay` makes every later caller get the same answer without a new request.
 */
@Service()
export class CategoriesApi {
  private readonly http = inject(HttpClient);
  private readonly url = `${inject(API_BASE_URL)}/categories`;

  private readonly categories$ = this.http
    .get<Category[]>(this.url)
    .pipe(shareReplay({ bufferSize: 1, refCount: false }));

  list(): Observable<Category[]> {
    return this.categories$;
  }
}
