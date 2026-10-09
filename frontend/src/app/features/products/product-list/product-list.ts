import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, inject, Injector, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { debounceTime, distinctUntilChanged, filter, finalize, map, switchMap } from 'rxjs';
import { ApiError } from '../../../core/http/api-error';
import { Notifications } from '../../../core/notifications/notifications';
import { integer, orderedRange } from '../../../shared/forms/validators';
import { ConfirmDialog, ConfirmDialogData } from '../../../shared/ui/confirm-dialog/confirm-dialog';
import {
  LOW_STOCK_THRESHOLD,
  Product,
  ProductFilterKind,
  StockOperation,
} from '../data-access/product-models';
import { ProductsStore } from '../data-access/products-store';
import {
  ProductFormDialog,
  ProductFormDialogData,
} from '../product-form-dialog/product-form-dialog';
import {
  StockAdjustDialog,
  StockAdjustDialogData,
} from '../stock-adjust-dialog/stock-adjust-dialog';
import { StockStatus } from '../stock-status/stock-status';

const FILTER_DEBOUNCE_MS = 300;

/**
 * The product catalogue screen: browse, filter (by name or stock level), page through and act on
 * products. It owns the {@link ProductsStore} for its lifetime; dialogs share it via the injector.
 */
@Component({
  selector: 'app-product-list',
  imports: [
    CurrencyPipe,
    DatePipe,
    ReactiveFormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatMenuModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatTableModule,
    MatTooltipModule,
    StockStatus,
  ],
  providers: [ProductsStore],
  templateUrl: './product-list.html',
  styleUrl: './product-list.scss',
})
export class ProductList {
  protected readonly store = inject(ProductsStore);
  private readonly dialog = inject(MatDialog);
  private readonly injector = inject(Injector);
  private readonly notifications = inject(Notifications);

  protected readonly displayedColumns = [
    'id',
    'name',
    'category',
    'price',
    'stock',
    'updatedAt',
    'actions',
  ];
  protected readonly pageSizeOptions = [10, 20, 50, 100];
  protected readonly lowStockThreshold = LOW_STOCK_THRESHOLD;

  protected readonly filterKind = signal<ProductFilterKind>('all');
  protected readonly nameControl = new FormControl('', { nonNullable: true });
  protected readonly stockRange = new FormGroup(
    {
      min: new FormControl<number | null>(null, [Validators.min(0), integer]),
      max: new FormControl<number | null>(null, [Validators.min(0), integer]),
    },
    { validators: orderedRange },
  );

  protected readonly pageIndex = computed(() => this.store.query().page - 1);
  /** Products with a stock request in flight, so their quick buttons can't be double-clicked. */
  protected readonly pendingIds = signal<ReadonlySet<number>>(new Set());

  constructor() {
    this.nameControl.valueChanges
      .pipe(
        debounceTime(FILTER_DEBOUNCE_MS),
        map((name) => name.trim()),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.applyFilter());

    this.stockRange.valueChanges
      .pipe(debounceTime(FILTER_DEBOUNCE_MS), takeUntilDestroyed())
      .subscribe(() => this.applyFilter());
  }

  protected setFilterKind(kind: ProductFilterKind): void {
    this.filterKind.set(kind);
    this.applyFilter();
  }

  protected setStockRange(min: number | null, max: number | null): void {
    this.stockRange.setValue({ min, max });
  }

  protected clearName(): void {
    this.nameControl.setValue('');
  }

  protected onPage(event: PageEvent): void {
    this.store.setPage(event.pageIndex + 1, event.pageSize);
  }

  protected openCreate(): void {
    this.openProductForm({}).subscribe((product) =>
      this.notifications.success(`Created "${product.name}" (#${product.id}).`),
    );
  }

  protected openEdit(product: Product): void {
    this.openProductForm({ product }).subscribe((saved) =>
      this.notifications.success(`Saved "${saved.name}".`),
    );
  }

  protected openStockDialog(product: Product, operation: StockOperation): void {
    this.dialog
      .open<StockAdjustDialog, StockAdjustDialogData, Product>(StockAdjustDialog, {
        data: { product, operation },
        injector: this.injector,
      })
      .afterClosed()
      .pipe(filter((updated): updated is Product => !!updated))
      .subscribe((updated) =>
        this.notifications.success(`"${updated.name}" now has ${updated.stockQuantity} in stock.`),
      );
  }

  /** One-click +1 / -1. The API decides atomically whether the change is allowed. */
  protected quickAdjust(product: Product, operation: StockOperation): void {
    this.setPending(product.id, true);
    this.store
      .adjustStock(product, operation, 1)
      .pipe(finalize(() => this.setPending(product.id, false)))
      .subscribe({ error: (error: ApiError) => this.notifications.error(error.message) });
  }

  protected confirmDelete(product: Product): void {
    this.dialog
      .open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
        data: {
          title: 'Delete product?',
          message: `"${product.name}" (#${product.id}) will be permanently removed.`,
          confirmLabel: 'Delete',
        },
      })
      .afterClosed()
      .pipe(
        filter(Boolean),
        switchMap(() => this.store.delete(product)),
      )
      .subscribe({
        next: () => this.notifications.success(`Deleted "${product.name}".`),
        error: (error: ApiError) =>
          this.notifications.error(
            error.status === 412
              ? `"${product.name}" was changed by someone else. Review it before deleting.`
              : error.message,
          ),
      });
  }

  protected isPending(id: number): boolean {
    return this.pendingIds().has(id);
  }

  private applyFilter(): void {
    switch (this.filterKind()) {
      case 'all':
        this.store.setFilter({ kind: 'all' });
        return;
      case 'name': {
        const name = this.nameControl.value.trim();
        this.store.setFilter(name ? { kind: 'name', name } : { kind: 'all' });
        return;
      }
      case 'stock': {
        if (this.stockRange.invalid) {
          return;
        }
        const { min, max } = this.stockRange.getRawValue();
        this.store.setFilter({ kind: 'stock', min, max });
        return;
      }
    }
  }

  private openProductForm(data: ProductFormDialogData) {
    return this.dialog
      .open<ProductFormDialog, ProductFormDialogData, Product>(ProductFormDialog, {
        data,
        injector: this.injector,
      })
      .afterClosed()
      .pipe(filter((product): product is Product => !!product));
  }

  private setPending(id: number, pending: boolean): void {
    this.pendingIds.update((ids) => {
      const next = new Set(ids);
      if (pending) {
        next.add(id);
      } else {
        next.delete(id);
      }
      return next;
    });
  }
}
