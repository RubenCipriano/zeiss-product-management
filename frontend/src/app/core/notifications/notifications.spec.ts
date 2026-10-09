import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Notifications } from './notifications';

describe('Notifications', () => {
  const snackBar = { open: vi.fn() };

  beforeEach(() => {
    snackBar.open.mockReset();
    TestBed.configureTestingModule({ providers: [{ provide: MatSnackBar, useValue: snackBar }] });
  });

  it('shows errors longer and with the error style', () => {
    const notifications = TestBed.inject(Notifications);

    notifications.success('Saved');
    notifications.error('Failed');

    const [, , successConfig] = snackBar.open.mock.calls[0];
    const [, , errorConfig] = snackBar.open.mock.calls[1];
    expect(errorConfig.duration).toBeGreaterThan(successConfig.duration);
    expect(errorConfig.panelClass).toBe('snackbar-error');
  });
});
