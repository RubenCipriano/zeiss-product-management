import { Component, inject } from '@angular/core';
import { Auth } from '../../../../core/auth/auth';
import { Icon, PageChange, Paginator, ProgressBar } from '../../../../shared/ui';
import { ProductsStore } from '../../data-access/products-store';
import { InventorySummary, ProductFilters, ProductTable } from '../../ui';
import { ProductActions } from '../product-actions';

/**
 * The product catalogue screen. A container: it owns the screen's state ({@link ProductsStore}) and the user's
 * actions ({@link ProductActions}) for its lifetime, and wires them to presentational components (summary, filters,
 * table, paginator). It renders no business logic of its own.
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
