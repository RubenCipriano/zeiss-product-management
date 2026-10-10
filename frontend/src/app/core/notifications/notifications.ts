// Toast messages ("Saved", "Could not delete"): the Notifications service any code calls to show one.
import { Service, signal } from '@angular/core';

export type ToastKind = 'success' | 'error';

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
}

/** How long a toast stays. Errors stay twice as long: they usually need reading. */
const DURATION_MS: Record<ToastKind, number> = { success: 4_000, error: 8_000 };

/** At most this many toasts on screen: a burst of errors must not cover the page. */
const MAX_VISIBLE = 4;

/**
 * Toast messages: short feedback such as "Saved" that disappears by itself. Any code can call `success()` or
 * `error()`; the toast outlet in the app shell shows the list. One shared instance for the whole app.
 */
@Service()
export class Notifications {
  private nextId = 1;
  /** A signal: the outlet's template re-reads it automatically when it changes. */
  private readonly items = signal<readonly Toast[]>([]);

  readonly toasts = this.items.asReadonly();

  success(message: string): void {
    this.show('success', message);
  }

  error(message: string): void {
    this.show('error', message);
  }

  dismiss(id: number): void {
    this.items.update((toasts) => toasts.filter((toast) => toast.id !== id));
  }

  private show(kind: ToastKind, message: string): void {
    const toast: Toast = { id: this.nextId++, kind, message };
    // The newest ones: the previous MAX_VISIBLE - 1 plus this one.
    this.items.update((toasts) => [...toasts.slice(-(MAX_VISIBLE - 1)), toast]);
    setTimeout(() => this.dismiss(toast.id), DURATION_MS[kind]);
  }
}
