import { Component, computed, input } from '@angular/core';
import { LOW_STOCK_THRESHOLD } from '../data-access/product-models';

type StockLevel = 'out' | 'low' | 'ok';

const LEVELS: Record<StockLevel, { label: string; badge: string; dot: string }> = {
  out: {
    label: 'Out of stock',
    badge: 'bg-red-50 text-red-700 ring-red-600/15',
    dot: 'bg-red-500',
  },
  low: {
    label: 'Low stock',
    badge: 'bg-amber-50 text-amber-800 ring-amber-600/20',
    dot: 'bg-amber-500',
  },
  ok: {
    label: 'In stock',
    badge: 'bg-emerald-50 text-emerald-700 ring-emerald-600/20',
    dot: 'bg-emerald-500',
  },
};

/** A stock quantity with an at-a-glance status. Purely presentational. */
@Component({
  selector: 'app-stock-status',
  template: `
    <span class="size-1.5 rounded-full" [class]="style().dot"></span>
    <span class="min-w-[2ch] text-right font-semibold tabular-nums">{{ quantity() }}</span>
    <span>{{ style().label }}</span>
  `,
  host: {
    class:
      'inline-flex min-w-32 items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium whitespace-nowrap ring-1 ring-inset',
    '[class]': 'style().badge + " stock-status--" + level()',
    '[attr.aria-label]': 'quantity() + " units, " + style().label',
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

  protected readonly style = computed(() => LEVELS[this.level()]);
}
