import { DestroyRef, DOCUMENT, inject } from '@angular/core';

export interface AutoRefreshOptions {
  /** Interval in milliseconds; 0 (or less) turns it off. */
  everyMs: number;
  /** Skip a tick when this returns false (e.g. while a request is already in flight). */
  when?: () => boolean;
}

/**
 * Calls `refresh` every `everyMs` while the browser tab is visible, and stops when the caller is destroyed.
 *
 * A reusable behaviour for any component or service: it uses `inject()` to find the caller's lifetime (`DestroyRef`),
 * so it must be called where `inject()` works: in a constructor or a field initializer.
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
