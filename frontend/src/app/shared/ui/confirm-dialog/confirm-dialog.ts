import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, inject } from '@angular/core';
import { Icon } from '../icon/icon';

export interface ConfirmDialogData {
  title: string;
  message: string;
  confirmLabel: string;
}

/** A reusable "Are you sure?" dialog for destructive actions (e.g. delete). Closes with `true` when confirmed. */
@Component({
  selector: 'app-confirm-dialog',
  imports: [Icon],
  templateUrl: './confirm-dialog.html',
})
export class ConfirmDialog {
  /** Id of the title: the dialog is opened with it as its accessible name (AppDialog). */
  static readonly TITLE_ID = 'confirm-dialog-title';
  protected readonly titleId = ConfirmDialog.TITLE_ID;

  protected readonly data = inject<ConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<boolean>>(DialogRef);
}
