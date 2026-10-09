import { Component, computed, input } from '@angular/core';
import { LOW_STOCK_THRESHOLD } from '../data-access/product.models';

type StockLevel = 'out' | 'low' | 'ok';

const LABELS: Record<StockLevel, string> = {
  out: 'Out of stock',
  low: 'Low stock',
  ok: 'In stock',
};

/** Shows a stock quantity together with an at-a-glance status. Purely presentational. */
@Component({
  selector: 'app-stock-status',
  templateUrl: './stock-status.html',
  styleUrl: './stock-status.scss',
  host: {
    '[class]': '"stock-status stock-status--" + level()',
    '[attr.aria-label]': 'quantity() + " units, " + label()',
  },
})
export class StockStatus {
  readonly quantity = input.required<number>();

  protected readonly level = computed<StockLevel>(() => {
    const quantity = this.quantity();
    if (quantity === 0) {
      return 'out';
    }
    return quantity <= LOW_STOCK_THRESHOLD ? 'low' : 'ok';
  });

  protected readonly label = computed(() => LABELS[this.level()]);
}
