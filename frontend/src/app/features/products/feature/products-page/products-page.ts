import { Component, inject } from '@angular/core';
import { Auth } from '../../../../core/auth/auth';
import { Icon, PageChange, Paginator, ProgressBar } from '../../../../shared/ui';
import { ProductsStore } from '../../data-access/products-store';
import { InventorySummary, ProductFilters, ProductTable } from '../../ui';
import { ProductActions } from '../product-actions';

/**
 * The products page: shows the summary cards, the filters, the table and the paginator. It owns no data and no logic:
 * it reads everything from {@link ProductsStore} and hands every user action (which may open a dialog) to
 * {@link ProductActions}. Both are created with the page (`providers`) and destroyed with it.
 */
@Component({
  selector: 'app-products-page',
  imports: [Icon, InventorySummary, Paginator, ProductFilters, ProductTable, ProgressBar],
  providers: [ProductsStore, ProductActions],
  templateUrl: './products-page.html',
})
export class ProductsPage {
  protected readonly store = inject(ProductsStore);
  protected readonly actions = inject(ProductActions);
  /** Used only to hide what the user may not do; the API enforces the same rules on every request. */
  protected readonly auth = inject(Auth);

  protected onPage(change: PageChange): void {
    this.store.setPage(change.page, change.pageSize);
  }
}
