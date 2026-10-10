import { Component, inject } from '@angular/core';
import { Notifications } from '../../../core/notifications/notifications';
import { Icon } from '../icon/icon';

/**
 * Shows the toast messages from {@link Notifications} at the bottom of the screen (placed once, in the app shell).
 * Screen readers announce them (`aria-live`).
 */
@Component({
  selector: 'app-toast-outlet',
  imports: [Icon],
  template: `
    <div
      class="pointer-events-none fixed inset-x-0 bottom-0 z-[1100] flex flex-col items-center gap-2 p-4 sm:items-end"
      aria-live="polite"
    >
      @for (toast of notifications.toasts(); track toast.id) {
        <div
          class="pointer-events-auto flex w-full max-w-sm items-start gap-3 rounded-xl bg-white p-4 text-sm shadow-lg ring-1"
          [class]="toast.kind === 'error' ? 'ring-red-200' : 'ring-slate-200'"
          [attr.role]="toast.kind === 'error' ? 'alert' : 'status'"
        >
          <app-icon
            [name]="toast.kind === 'error' ? 'alert' : 'check'"
            class="mt-0.5 size-5"
            [class]="toast.kind === 'error' ? 'text-red-600' : 'text-emerald-600'"
          />
          <p class="flex-1 text-slate-700">{{ toast.message }}</p>
          <button
            type="button"
            class="btn-icon -m-1 size-7"
            aria-label="Dismiss"
            (click)="notifications.dismiss(toast.id)"
          >
            <app-icon name="x" />
          </button>
        </div>
      }
    </div>
  `,
})
export class ToastOutlet {
  protected readonly notifications = inject(Notifications);
}
