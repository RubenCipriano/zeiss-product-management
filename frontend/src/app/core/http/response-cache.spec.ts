import { HttpResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ResponseCache } from './response-cache';

describe('ResponseCache', () => {
  it('evicts the least recently used entry when full', () => {
    const cache = TestBed.inject(ResponseCache);
    for (let i = 0; i < ResponseCache.MAX_ENTRIES; i++) {
      cache.set(`/api/${i}`, new HttpResponse({ body: i }));
    }

    cache.get('/api/0'); // used recently: survives
    cache.set('/api/new', new HttpResponse({ body: 'new' }));

    expect(cache.size).toBe(ResponseCache.MAX_ENTRIES);
    expect(cache.get('/api/0')).toBeDefined();
    expect(cache.get('/api/1')).toBeUndefined();
  });
});
