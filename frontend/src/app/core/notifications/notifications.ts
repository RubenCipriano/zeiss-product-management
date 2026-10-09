import { inject, Service } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';

/** Short, non-blocking feedback for completed actions. */
@Service()
export class Notifications {
  private readonly snackBar = inject(MatSnackBar);

  success(message: string): void {
    this.snackBar.open(message, 'Dismiss', { duration: 4000 });
  }

  error(message: string): void {
    this.snackBar.open(message, 'Dismiss', { duration: 8000, panelClass: 'snackbar-error' });
  }
}
