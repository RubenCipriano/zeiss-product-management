import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { SignedNumberPipe } from '../../../../shared/pipes';
import { Icon, PageChange, Paginator, ProgressBar } from '../../../../shared/ui';
import { Product } from '../../data-access/product-models';
import { MovementReasonPipe } from '../../ui/pipes';
import { ProductsApi } from '../../data-access/products-api';

export interface StockHistoryDialogData {
  product: Product;
}

/** Page sizes offered for the history; the first is the default. */
const PAGE_SIZES = [10, 25, 50] as const;

/**
 * The "Stock history" dialog: every stock change of one product, who made it and when, newest first, page by page.
 *
 * Loads its pages with its own `rxResource` (a loader that re-runs its request whenever `params` change) rather than
 * through `ProductsStore`: the history belongs to one product, is needed only while this dialog is open and is shared
 * with nothing else on screen, so it lives and dies with the dialog.
 */
@Component({
  selector: 'app-stock-history-dialog',
  imports: [DatePipe, Icon, Paginator, ProgressBar, MovementReasonPipe, SignedNumberPipe],
  templateUrl: './stock-history-dialog.html',
})
export class StockHistoryDialog {
  /** Id of the title: the dialog is opened with it as its accessible name (AppDialog). */
  static readonly TITLE_ID = 'stock-history-title';
  protected readonly titleId = StockHistoryDialog.TITLE_ID;

  private readonly api = inject(ProductsApi);
  protected readonly dialogRef = inject<DialogRef<void, StockHistoryDialog>>(DialogRef);
  protected readonly product = inject<StockHistoryDialogData>(DIALOG_DATA).product;

  protected readonly pageSizes = PAGE_SIZES;
  protected readonly page = signal<PageChange>({ page: 1, pageSize: PAGE_SIZES[0] });

  protected readonly history = rxResource({
    params: () => this.page(),
    stream: ({ params }) => this.api.stockMovements(this.product.id, params.page, params.pageSize),
  });

  protected onPage(change: PageChange): void {
    this.page.set(change);
  }
}
