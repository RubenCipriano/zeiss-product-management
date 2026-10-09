import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ApiError } from '../../../core/http/api-error';
import { integer } from '../../../shared/forms/validators';
import { validationMessage } from '../../../shared/forms/validation-message';
import { PRODUCT_LIMITS, Product, StockOperation } from '../data-access/product.models';
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
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    StockStatus,
  ],
  templateUrl: './stock-adjust-dialog.html',
  styleUrl: './stock-adjust-dialog.scss',
})
export class StockAdjustDialog {
  private readonly data = inject<StockAdjustDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject<MatDialogRef<StockAdjustDialog, Product>>(MatDialogRef);
  private readonly store = inject(ProductsStore);

  protected readonly product = this.data.product;
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
    this.saving.set(true);
    this.errorMessage.set(null);

    this.store.adjustStock(this.product, operation, quantity!).subscribe({
      next: (updated) => this.dialogRef.close(updated),
      error: (error: ApiError) => {
        this.saving.set(false);
        this.errorMessage.set(error.fieldError('quantity') ?? error.message);
      },
    });
  }
}
