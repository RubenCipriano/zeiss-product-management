import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Icon } from '../../../shared/ui/icon/icon';
import { PageChange, Paginator } from '../../../shared/ui/paginator/paginator';
import { Product, StockMovementReason } from '../data-access/product-models';
import { ProductsApi } from '../data-access/products-api';

export interface StockHistoryDialogData {
  product: Product;
}

const REASONS: Record<StockMovementReason, { label: string; style: string }> = {
  InitialStock: { label: 'Initial stock', style: 'bg-slate-100 text-slate-700' },
  StockAdded: { label: 'Added', style: 'bg-emerald-50 text-emerald-700' },
  StockRemoved: { label: 'Removed', style: 'bg-red-50 text-red-700' },
  Correction: { label: 'Correction', style: 'bg-amber-50 text-amber-800' },
};

/** A product's stock ledger: every change, who made it and when, newest first. */
@Component({
  selector: 'app-stock-history-dialog',
  imports: [DatePipe, Icon, Paginator],
  templateUrl: './stock-history-dialog.html',
})
export class StockHistoryDialog {
  private readonly api = inject(ProductsApi);
  protected readonly dialogRef = inject<DialogRef<void, StockHistoryDialog>>(DialogRef);
  protected readonly product = inject<StockHistoryDialogData>(DIALOG_DATA).product;
  protected readonly reasons = REASONS;

  protected readonly page = signal({ page: 1, pageSize: 10 });

  protected readonly history = rxResource({
    params: () => this.page(),
    stream: ({ params }) => this.api.stockMovements(this.product.id, params.page, params.pageSize),
  });

  protected onPage(change: PageChange): void {
    this.page.set(change);
  }
}
