import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { CategoriesApi } from './categories-api';

describe('CategoriesApi', () => {
  it('fetches categories once and shares the result', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const api = TestBed.inject(CategoriesApi);
    const httpTesting = TestBed.inject(HttpTestingController);
    const received: unknown[] = [];

    api.list().subscribe((categories) => received.push(categories));
    httpTesting.expectOne('/api/categories').flush([{ id: 1, name: 'Frames' }]);
    api.list().subscribe((categories) => received.push(categories));

    httpTesting.verify();
    expect(received).toEqual([[{ id: 1, name: 'Frames' }], [{ id: 1, name: 'Frames' }]]);
  });
});
