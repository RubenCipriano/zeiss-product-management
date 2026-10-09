import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, inject } from '@angular/core';
import { Icon } from '../icon/icon';

export interface ConfirmDialogData {
  title: string;
  message: string;
  confirmLabel: string;
}

/** Generic destructive-action confirmation. Closes with `true` when confirmed. */
@Component({
  selector: 'app-confirm-dialog',
  imports: [Icon],
  templateUrl: './confirm-dialog.html',
})
export class ConfirmDialog {
  protected readonly data = inject<ConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
}
