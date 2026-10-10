// Storage for the browser-side API cache (ResponseCache); the rules for using it are in client-cache-interceptor.ts.
import { HttpResponse } from '@angular/common/http';
import { Service } from '@angular/core';

interface Entry {
  response: HttpResponse<unknown>;
  storedAt: number;
}

/**
 * Where the browser-side cache keeps API responses (see clientCacheInterceptor): an in-memory map for this tab, at most
 * MAX_ENTRIES, dropping the least recently used first. Never persisted, so data from one session never reaches the
 * next. Cleared when the user signs out and after any change made through the API.
 */
@Service()
export class ResponseCache {
  static readonly MAX_ENTRIES = 200;

  private readonly entries = new Map<string, Entry>();

  get size(): number {
    return this.entries.size;
  }

  get(key: string): Entry | undefined {
    const entry = this.entries.get(key);
    if (entry) {
      // Map keeps insertion order: re-inserting marks the entry as most recently used.
      this.entries.delete(key);
      this.entries.set(key, entry);
    }
    return entry;
  }

  set(key: string, response: HttpResponse<unknown>): void {
    this.entries.delete(key);
    this.entries.set(key, { response, storedAt: Date.now() });
    while (this.entries.size > ResponseCache.MAX_ENTRIES) {
      this.entries.delete(this.entries.keys().next().value!);
    }
  }

  clear(): void {
    this.entries.clear();
  }
}
