import { inject, Injector, Service, signal } from '@angular/core';
import { filter, finalize, Observable, switchMap } from 'rxjs';
import { ApiError } from '../../../core/http/api-error';
import { Notifications } from '../../../core/notifications/notifications';
import { AppDialog, ConfirmDialog, ConfirmDialogData } from '../../../shared/ui';
import { Product, StockOperation } from '../data-access/product-models';
import { ProductsStore } from '../data-access/products-store';
import {
  ProductFormDialog,
  ProductFormDialogData,
} from './product-form-dialog/product-form-dialog';
import {
  StockAdjustDialog,
  StockAdjustDialogData,
} from './stock-adjust-dialog/stock-adjust-dialog';
import {
  StockHistoryDialog,
  StockHistoryDialogData,
} from './stock-history-dialog/stock-history-dialog';

/**
 * What the user can do on the products page (create, edit, delete, change stock, see history): each action opens the
 * right dialog (or calls the store directly), then shows a toast saying how it went.
 *
 * Provided by the page, next to its {@link ProductsStore}. Dialogs are opened with the page's injector (the page's
 * own set of services), so they use that same store. The table and the page only say *what* the user asked for; how
 * it is carried out (dialogs, idempotency keys, messages) lives here, in one place, tested on its own.
 */
@Service({ autoProvided: false })
export class ProductActions {
  private readonly store = inject(ProductsStore);
  private readonly dialogs = inject(AppDialog);
  private readonly notifications = inject(Notifications);
  private readonly injector = inject(Injector);

  /** Products with a quick stock change in flight. */
  readonly pendingIds = signal<ReadonlySet<number>>(new Set());

  create(): void {
    this.openForm({}).subscribe((product) =>
      this.notifications.success(`Created "${product.name}" (#${product.id}).`),
    );
  }

  edit(product: Product): void {
    this.openForm({ product }).subscribe((saved) =>
      this.notifications.success(`Saved "${saved.name}".`),
    );
  }

  adjustStock(product: Product, operation: StockOperation = 'remove'): void {
    this.dialogs
      .open<Product, StockAdjustDialogData, StockAdjustDialog>(StockAdjustDialog, {
        data: { product, operation },
        labelledBy: StockAdjustDialog.TITLE_ID,
        injector: this.injector,
      })
      .closed.pipe(filter((updated): updated is Product => !!updated))
      .subscribe((updated) =>
        this.notifications.success(`"${updated.name}" now has ${updated.stockQuantity} in stock.`),
      );
  }

  /** The table's + / − buttons: one click, no dialog. A new idempotency key per click: each click is a new change. */
  quickChange(product: Product, operation: StockOperation, quantity: number): void {
    this.setPending(product.id, true);
    this.store
      .adjustStock(product, operation, quantity, crypto.randomUUID())
      .pipe(finalize(() => this.setPending(product.id, false)))
      .subscribe({ error: (error: ApiError) => this.notifications.error(error.message) });
  }

  showHistory(product: Product): void {
    this.dialogs.open<void, StockHistoryDialogData, StockHistoryDialog>(StockHistoryDialog, {
      data: { product },
      labelledBy: StockHistoryDialog.TITLE_ID,
    });
  }

  delete(product: Product): void {
    this.dialogs
      .open<boolean, ConfirmDialogData, ConfirmDialog>(ConfirmDialog, {
        data: {
          title: 'Delete product?',
          message: `"${product.name}" (#${product.id}) will be permanently removed.`,
          confirmLabel: 'Delete',
        },
        labelledBy: ConfirmDialog.TITLE_ID,
        role: 'alertdialog',
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

  private openForm(data: ProductFormDialogData): Observable<Product> {
    return this.dialogs
      .open<Product, ProductFormDialogData, ProductFormDialog>(ProductFormDialog, {
        data,
        labelledBy: ProductFormDialog.TITLE_ID,
        injector: this.injector,
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
