import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { SignedNumberPipe } from '../../../shared/pipes';
import { Icon, PageChange, Paginator, ProgressBar } from '../../../shared/ui';
import { Product } from '../data-access/product-models';
import { MovementReasonPipe } from '../pipes';
import { ProductsApi } from '../data-access/products-api';

export interface StockHistoryDialogData {
  product: Product;
}

/** A product's stock ledger: every change, who made it and when, newest first. */
@Component({
  selector: 'app-stock-history-dialog',
  imports: [DatePipe, Icon, Paginator, ProgressBar, MovementReasonPipe, SignedNumberPipe],
  templateUrl: './stock-history-dialog.html',
})
export class StockHistoryDialog {
  private readonly api = inject(ProductsApi);
  protected readonly dialogRef = inject<DialogRef<void, StockHistoryDialog>>(DialogRef);
  protected readonly product = inject<StockHistoryDialogData>(DIALOG_DATA).product;

  protected readonly page = signal({ page: 1, pageSize: 10 });

  protected readonly history = rxResource({
    params: () => this.page(),
    stream: ({ params }) => this.api.stockMovements(this.product.id, params.page, params.pageSize),
  });

  protected onPage(change: PageChange): void {
    this.page.set(change);
  }
}
