import { CdkMenu, CdkMenuItem, CdkMenuTrigger } from '@angular/cdk/menu';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, input, output, signal } from '@angular/core';
import { Icon } from '../../../../shared/ui';
import { Product, StockOperation } from '../../data-access/product-models';
import { StockStatus } from '../stock-status/stock-status';

/** A quick + / − click: move `quantity` units of `product`. */
export interface QuickStockChange {
  product: Product;
  operation: StockOperation;
  quantity: number;
}

/**
 * The products table: one row per product, with the quick + / − stock buttons and each row's action menu (Angular CDK
 * menu). Presentational: it shows what it receives through `input()`s and reports what the user asked for through
 * `output()`s; the page decides what happens.
 */
@Component({
  selector: 'app-product-table',
  imports: [CdkMenu, CdkMenuItem, CdkMenuTrigger, CurrencyPipe, DatePipe, Icon, StockStatus],
  templateUrl: './product-table.html',
})
export class ProductTable {
  readonly products = input.required<readonly Product[]>();
  /** No rows yet because the first page is loading: show placeholders instead of "nothing found". */
  readonly loading = input(false);
  readonly canChangeStock = input(false);
  readonly canEditProducts = input(false);
  /** Products with a stock change in flight: their quick buttons are disabled. */
  readonly pendingIds = input<ReadonlySet<number>>(new Set());

  readonly quickChange = output<QuickStockChange>();
  readonly adjustStock = output<Product>();
  readonly showHistory = output<Product>();
  readonly edit = output<Product>();
  readonly remove = output<Product>();

  /** Units moved by each + / − click. Any other amount: "Adjust stock…" in the row menu. */
  protected readonly steps = [1, 5, 10, 25, 100] as const;
  protected readonly step = signal(1);
  protected readonly unitsLabel = computed(() =>
    this.step() === 1 ? 'one unit' : `${this.step()} units`,
  );
  /** Grey placeholder rows shown while the first page loads. */
  protected readonly skeletonRows = [1, 2, 3, 4, 5];

  protected setStep(event: Event): void {
    this.step.set(Number((event.target as HTMLSelectElement).value));
  }

  protected change(product: Product, operation: StockOperation): void {
    this.quickChange.emit({ product, operation, quantity: this.step() });
  }
}
