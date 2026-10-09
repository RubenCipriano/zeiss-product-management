import { TestBed } from '@angular/core/testing';
import { Notifications } from './notifications';

describe('Notifications', () => {
  let notifications: Notifications;

  beforeEach(() => {
    vi.useFakeTimers();
    notifications = TestBed.inject(Notifications);
  });

  afterEach(() => vi.useRealTimers());

  it('dismisses success messages after 4s and errors after 8s', () => {
    notifications.success('Saved');
    notifications.error('Failed');

    vi.advanceTimersByTime(4_000);
    expect(notifications.toasts().map((toast) => toast.message)).toEqual(['Failed']);

    vi.advanceTimersByTime(4_000);
    expect(notifications.toasts()).toEqual([]);
  });

  it('keeps only the newest four messages', () => {
    for (let i = 1; i <= 6; i++) {
      notifications.error(`Error ${i}`);
    }

    expect(notifications.toasts().map((toast) => toast.message)).toEqual([
      'Error 3',
      'Error 4',
      'Error 5',
      'Error 6',
    ]);
  });

  it('can be dismissed early', () => {
    notifications.success('Saved');

    notifications.dismiss(notifications.toasts()[0].id);

    expect(notifications.toasts()).toEqual([]);
  });
});
