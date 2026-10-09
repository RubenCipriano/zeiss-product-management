import { Dialog } from '@angular/cdk/dialog';
import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, inject, Injector, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { debounceTime, distinctUntilChanged, filter, finalize, map, switchMap } from 'rxjs';
import { Auth } from '../../../core/auth/auth';
import { ApiError } from '../../../core/http/api-error';
import { Notifications } from '../../../core/notifications/notifications';
import { integer, orderedRange } from '../../../shared/forms/validators';
import { ConfirmDialog, ConfirmDialogData } from '../../../shared/ui/confirm-dialog/confirm-dialog';
import { Icon, IconName } from '../../../shared/ui/icon/icon';
import { PageChange, Paginator } from '../../../shared/ui/paginator/paginator';
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
import {
  StockHistoryDialog,
  StockHistoryDialogData,
} from '../stock-history-dialog/stock-history-dialog';
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
    CdkMenu,
    CdkMenuItem,
    CdkMenuTrigger,
    Icon,
    Paginator,
    StockStatus,
  ],
  providers: [ProductsStore],
  templateUrl: './product-list.html',
})
export class ProductList {
  protected readonly store = inject(ProductsStore);
  /** Used only to hide what the user may not do; the API enforces the same rules on every request. */
  protected readonly auth = inject(Auth);
  private readonly dialog = inject(Dialog);
  private readonly injector = inject(Injector);
  private readonly notifications = inject(Notifications);

  protected readonly lowStockThreshold = LOW_STOCK_THRESHOLD;
  protected readonly filterOptions: readonly { kind: ProductFilterKind; label: string }[] = [
    { kind: 'all', label: 'All products' },
    { kind: 'name', label: 'Search by name' },
    { kind: 'stock', label: 'Stock level' },
  ];
  protected readonly skeletonRows = [1, 2, 3, 4, 5];

  protected readonly filterKind = signal<ProductFilterKind>('all');
  protected readonly nameControl = new FormControl('', { nonNullable: true });
  protected readonly stockRange = new FormGroup(
    {
      min: new FormControl<number | null>(null, [Validators.min(0), integer]),
      max: new FormControl<number | null>(null, [Validators.min(0), integer]),
    },
    { validators: orderedRange },
  );

  protected readonly summaryCards = computed(() => {
    const summary = this.store.summary();
    return [
      {
        label: 'Products',
        value: summary?.totalProducts,
        icon: 'package' as IconName,
        accent: 'bg-brand-50 text-brand-600',
        apply: () => this.setFilterKind('all'),
      },
      {
        label: `Low stock (≤ ${LOW_STOCK_THRESHOLD})`,
        value: summary?.lowStock,
        icon: 'trendingDown' as IconName,
        accent: 'bg-amber-50 text-amber-600',
        apply: () => this.showStockRange(0, LOW_STOCK_THRESHOLD),
      },
      {
        label: 'Out of stock',
        value: summary?.outOfStock,
        icon: 'ban' as IconName,
        accent: 'bg-red-50 text-red-600',
        apply: () => this.showStockRange(0, 0),
      },
    ];
  });
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

  /** Summary-card shortcut: switch to the stock filter with a preset range. */
  protected showStockRange(min: number, max: number): void {
    this.filterKind.set('stock');
    this.stockRange.setValue({ min, max }, { emitEvent: false });
    this.applyFilter();
  }

  protected clearName(): void {
    this.nameControl.setValue('');
  }

  protected onPage(change: PageChange): void {
    this.store.setPage(change.page, change.pageSize);
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
      .open<Product, StockAdjustDialogData, StockAdjustDialog>(StockAdjustDialog, {
        data: { product, operation },
        injector: this.injector,
        backdropClass: 'dialog-backdrop',
      })
      .closed.pipe(filter((updated): updated is Product => !!updated))
      .subscribe((updated) =>
        this.notifications.success(`"${updated.name}" now has ${updated.stockQuantity} in stock.`),
      );
  }

  protected openHistory(product: Product): void {
    this.dialog.open<void, StockHistoryDialogData, StockHistoryDialog>(StockHistoryDialog, {
      data: { product },
      backdropClass: 'dialog-backdrop',
    });
  }

  /** One-click +1 / -1. The API decides atomically whether the change is allowed. */
  protected quickAdjust(product: Product, operation: StockOperation): void {
    this.setPending(product.id, true);
    this.store
      .adjustStock(product, operation, 1, crypto.randomUUID())
      .pipe(finalize(() => this.setPending(product.id, false)))
      .subscribe({ error: (error: ApiError) => this.notifications.error(error.message) });
  }

  protected confirmDelete(product: Product): void {
    this.dialog
      .open<boolean, ConfirmDialogData, ConfirmDialog>(ConfirmDialog, {
        data: {
          title: 'Delete product?',
          message: `"${product.name}" (#${product.id}) will be permanently removed.`,
          confirmLabel: 'Delete',
        },
        backdropClass: 'dialog-backdrop',
      })
      .closed.pipe(
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
      .open<Product, ProductFormDialogData, ProductFormDialog>(ProductFormDialog, {
        data,
        injector: this.injector,
        backdropClass: 'dialog-backdrop',
      })
      .closed.pipe(filter((product): product is Product => !!product));
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
