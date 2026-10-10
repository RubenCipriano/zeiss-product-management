import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { ComponentType } from '@angular/cdk/portal';
import { inject, Injector, Service } from '@angular/core';

export interface AppDialogOptions<D> {
  data: D;
  /** Id of the dialog's title element: screen readers announce the dialog by that name. */
  labelledBy: string;
  /** `alertdialog` for confirmations that interrupt the user. */
  role?: 'dialog' | 'alertdialog';
  /** Lets the dialog use providers of the caller, e.g. a store provided by the page. */
  injector?: Injector;
}

/**
 * Opens every dialog of the app the same way, on top of the Angular CDK dialog (the Component Dev Kit: unstyled,
 * accessible building blocks from the Angular team). The CDK keeps keyboard focus inside the dialog and returns it to
 * the button that opened it; this class adds the accessible name and the app's backdrop. Dialog components therefore
 * only render their content (no `role` of their own: one dialog, one name).
 */
@Service()
export class AppDialog {
  private readonly dialog = inject(Dialog);

  open<R, D, C>(component: ComponentType<C>, options: AppDialogOptions<D>): DialogRef<R, C> {
    return this.dialog.open<R, D, C>(component, {
      data: options.data,
      injector: options.injector,
      ariaLabelledBy: options.labelledBy,
      role: options.role ?? 'dialog',
      backdropClass: 'dialog-backdrop',
    });
  }
}
