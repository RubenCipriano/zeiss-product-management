import { DestroyRef, DOCUMENT, inject } from '@angular/core';

export interface AutoRefreshOptions {
  /** Interval in milliseconds; 0 (or less) turns it off. */
  everyMs: number;
  /** Skip a tick when this returns false (e.g. while a request is already in flight). */
  when?: () => boolean;
}

/**
 * Calls `refresh` periodically while the browser tab is visible, and stops when the caller is destroyed.
 *
 * Angular's equivalent of a React hook: an `inject…`-style function that adds a behaviour to whichever component or
 * service calls it, using that caller's lifetime (`DestroyRef`). Must be called in an injection context (a
 * constructor or field initializer).
 */
export function autoRefresh(
  refresh: () => void,
  { everyMs, when = () => true }: AutoRefreshOptions,
): void {
  if (everyMs <= 0) {
    return;
  }
  const document = inject(DOCUMENT);
  const timer = setInterval(() => {
    if (document.visibilityState === 'visible' && when()) {
      refresh();
    }
  }, everyMs);
  inject(DestroyRef).onDestroy(() => clearInterval(timer));
}
