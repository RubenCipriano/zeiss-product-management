import { Service, signal } from '@angular/core';

export type ToastKind = 'success' | 'error';

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
}

const DURATION_MS: Record<ToastKind, number> = { success: 4_000, error: 8_000 };

/** Short, non-blocking feedback for completed actions, rendered by the toast outlet in the app shell. */
@Service()
export class Notifications {
  private nextId = 1;
  private readonly items = signal<readonly Toast[]>([]);

  readonly toasts = this.items.asReadonly();

  success(message: string): void {
    this.show('success', message);
  }

  /** Errors stay twice as long: they usually need reading. */
  error(message: string): void {
    this.show('error', message);
  }

  dismiss(id: number): void {
    this.items.update((toasts) => toasts.filter((toast) => toast.id !== id));
  }

  private show(kind: ToastKind, message: string): void {
    const toast: Toast = { id: this.nextId++, kind, message };
    // Keep the newest few: a burst of errors must not cover the screen.
    this.items.update((toasts) => [...toasts.slice(-3), toast]);
    setTimeout(() => this.dismiss(toast.id), DURATION_MS[kind]);
  }
}
