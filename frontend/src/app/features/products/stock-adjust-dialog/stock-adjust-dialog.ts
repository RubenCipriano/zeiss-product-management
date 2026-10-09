import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ApiError } from '../../../core/http/api-error';
import { integer } from '../../../shared/forms/validators';
import { validationMessage } from '../../../shared/forms/validation-message';
import { Icon, IconName } from '../../../shared/ui/icon/icon';
import { PRODUCT_LIMITS, Product, StockOperation } from '../data-access/product-models';
import { ProductsStore } from '../data-access/products-store';
import { StockStatus } from '../stock-status/stock-status';

export interface StockAdjustDialogData {
  product: Product;
  operation: StockOperation;
}

/**
 * Adds or removes a quantity of stock. The API applies the change atomically; this dialog only
 * previews the result and shows the API's answer (e.g. "only 3 available").
 *
 * Opened with the product list's injector, so it shares the screen's {@link ProductsStore}.
 */
@Component({
  selector: 'app-stock-adjust-dialog',
  imports: [ReactiveFormsModule, Icon, StockStatus],
  templateUrl: './stock-adjust-dialog.html',
})
export class StockAdjustDialog {
  private readonly data = inject<StockAdjustDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<Product, StockAdjustDialog>>(DialogRef);
  private readonly store = inject(ProductsStore);

  protected readonly operationOptions: readonly {
    value: StockOperation;
    label: string;
    icon: IconName;
  }[] = [
    { value: 'remove', label: 'Remove stock', icon: 'minus' },
    { value: 'add', label: 'Add stock', icon: 'plus' },
  ];

  protected readonly product = this.data.product;
  /** One key for this dialog: resubmitting after an error can never apply the movement twice. */
  private readonly idempotencyKey = crypto.randomUUID();
  protected readonly saving = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly form = new FormGroup({
    operation: new FormControl<StockOperation>(this.data.operation, { nonNullable: true }),
    quantity: new FormControl<number | null>(1, [
      Validators.required,
      integer,
      Validators.min(1),
      Validators.max(PRODUCT_LIMITS.maxStock),
    ]),
  });

  private readonly formValue = toSignal(this.form.valueChanges, {
    initialValue: this.form.getRawValue(),
  });

  protected readonly operation = computed(() => this.formValue().operation);

  /** Stock after the change, or null when the input is not a valid quantity yet. */
  protected readonly resultingStock = computed(() => {
    const { operation, quantity } = this.formValue();
    if (!quantity || quantity < 1 || !Number.isInteger(quantity)) {
      return null;
    }
    return operation === 'remove'
      ? this.product.stockQuantity - quantity
      : this.product.stockQuantity + quantity;
  });

  protected setOperation(operation: StockOperation): void {
    this.form.controls.operation.setValue(operation);
  }

  protected quantityError(): string | null {
    const control = this.form.controls.quantity;
    return control.touched || control.dirty ? validationMessage(control.errors, 'Quantity') : null;
  }

  protected submit(): void {
    if (this.form.invalid || this.saving()) {
      this.form.markAllAsTouched();
      return;
    }

    const { operation, quantity } = this.form.getRawValue();
    this.setSaving(true);
    this.errorMessage.set(null);

    this.store.adjustStock(this.product, operation, quantity!, this.idempotencyKey).subscribe({
      next: (updated) => this.dialogRef.close(updated),
      error: (error: ApiError) => {
        this.setSaving(false);
        this.errorMessage.set(error.fieldError('quantity') ?? error.message);
      },
    });
  }

  /** While a request is in flight the dialog cannot be dismissed, so its outcome is never lost. */
  private setSaving(saving: boolean): void {
    this.saving.set(saving);
    this.dialogRef.disableClose = saving;
  }
}
