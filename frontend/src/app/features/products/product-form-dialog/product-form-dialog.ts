import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { catchError, of } from 'rxjs';
import { ApiError } from '../../../core/http/api-error';
import { integer, maxDecimals, notBlank } from '../../../shared/forms/validators';
import { validationMessage } from '../../../shared/forms/validation-message';
import { CategoriesApi } from '../data-access/categories-api';
import { PRODUCT_LIMITS, Product, ProductInput } from '../data-access/product-models';
import { ProductsApi } from '../data-access/products-api';
import { ProductsStore } from '../data-access/products-store';

export interface ProductFormDialogData {
  /** The product to edit; omit to create a new one. */
  product?: Product;
}

type ProductField = keyof ProductInput;

const FIELD_LABELS: Record<ProductField, string> = {
  name: 'Name',
  description: 'Description',
  price: 'Price',
  stockQuantity: 'Stock',
  categoryId: 'Category',
};

/**
 * Create / edit form. Client-side validation mirrors the API rules for fast feedback, but the
 * API remains authoritative: its field errors are shown on the matching controls.
 *
 * Opened with the product list's injector, so it shares the screen's {@link ProductsStore}.
 */
@Component({
  selector: 'app-product-form-dialog',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatProgressBarModule,
  ],
  templateUrl: './product-form-dialog.html',
  styleUrl: './product-form-dialog.scss',
})
export class ProductFormDialog {
  private readonly store = inject(ProductsStore);
  private readonly productsApi = inject(ProductsApi);
  private readonly dialogRef = inject<MatDialogRef<ProductFormDialog, Product>>(MatDialogRef);

  protected readonly limits = PRODUCT_LIMITS;
  protected readonly categories = toSignal(
    inject(CategoriesApi)
      .list()
      .pipe(catchError(() => of([]))),
    { initialValue: [] },
  );

  /** The version being edited. Replaced when the user reloads after a conflict. */
  protected readonly editing = signal<Product | null>(
    inject<ProductFormDialogData>(MAT_DIALOG_DATA).product ?? null,
  );
  protected readonly isEdit = computed(() => this.editing() !== null);
  protected readonly saving = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  /** Set when the product changed on the server since it was opened (HTTP 412). */
  protected readonly conflict = signal(false);

  protected readonly form = new FormGroup({
    name: new FormControl('', {
      nonNullable: true,
      validators: [
        Validators.required,
        notBlank,
        Validators.maxLength(PRODUCT_LIMITS.nameMaxLength),
      ],
    }),
    description: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(PRODUCT_LIMITS.descriptionMaxLength)],
    }),
    price: new FormControl<number | null>(null, [
      Validators.required,
      Validators.min(PRODUCT_LIMITS.minPrice),
      Validators.max(PRODUCT_LIMITS.maxPrice),
      maxDecimals(2),
    ]),
    stockQuantity: new FormControl<number | null>(0, [
      Validators.required,
      integer,
      Validators.min(0),
      Validators.max(PRODUCT_LIMITS.maxStock),
    ]),
    categoryId: new FormControl<number | null>(null, [Validators.required]),
  });

  constructor() {
    const product = this.editing();
    if (product) {
      this.fillForm(product);
    }
  }

  protected fieldError(field: ProductField): string | null {
    const control = this.form.controls[field];
    return control.touched || control.dirty
      ? validationMessage(control.errors, FIELD_LABELS[field])
      : null;
  }

  protected submit(): void {
    if (this.form.invalid || this.saving()) {
      this.form.markAllAsTouched();
      return;
    }

    const input = this.toInput();
    const product = this.editing();
    const request$ = product ? this.store.update(product, input) : this.store.create(input);

    this.setSaving(true);
    this.errorMessage.set(null);
    request$.subscribe({
      next: (saved) => this.dialogRef.close(saved),
      error: (error: ApiError) => this.handleError(error),
    });
  }

  /** After a 412, load the latest version so the user can re-apply their change on top of it. */
  protected reloadLatest(): void {
    const product = this.editing();
    if (!product) {
      return;
    }

    this.setSaving(true);
    this.productsApi.getById(product.id).subscribe({
      next: (latest) => {
        this.editing.set(latest);
        this.fillForm(latest);
        this.conflict.set(false);
        this.errorMessage.set(null);
        this.setSaving(false);
      },
      error: (error: ApiError) => this.handleError(error),
    });
  }

  private handleError(error: ApiError): void {
    this.setSaving(false);

    if (error.status === 412) {
      this.conflict.set(true);
      return;
    }

    const fieldErrorsShown = this.showFieldErrors(error);
    if (!fieldErrorsShown) {
      this.errorMessage.set(error.message);
    }
  }

  /** Puts the API's per-field messages on the matching controls. Returns whether any matched. */
  private showFieldErrors(error: ApiError): boolean {
    let matched = false;
    for (const field of Object.keys(this.form.controls) as ProductField[]) {
      const message = error.fieldError(field);
      if (message) {
        const control = this.form.controls[field];
        control.setErrors({ server: message });
        control.markAsTouched();
        matched = true;
      }
    }
    return matched;
  }

  private fillForm(product: Product): void {
    this.form.reset({
      name: product.name,
      description: product.description ?? '',
      price: product.price,
      stockQuantity: product.stockQuantity,
      categoryId: product.categoryId,
    });
  }

  private toInput(): ProductInput {
    const value = this.form.getRawValue();
    return {
      name: value.name.trim(),
      description: value.description.trim() || null,
      price: value.price!,
      stockQuantity: value.stockQuantity!,
      categoryId: value.categoryId!,
    };
  }

  /** While a request is in flight the dialog cannot be dismissed, so its outcome is never lost. */
  private setSaving(saving: boolean): void {
    this.saving.set(saving);
    this.dialogRef.disableClose = saving;
  }
}
