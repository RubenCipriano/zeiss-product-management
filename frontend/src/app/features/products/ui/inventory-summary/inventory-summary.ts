import { Component, computed, input, output } from '@angular/core';
import { Icon, IconName } from '../../../../shared/ui';
import {
  InventorySummary as Summary,
  LOW_STOCK_FILTER,
  LOW_STOCK_THRESHOLD,
  OUT_OF_STOCK_FILTER,
  ProductFilter,
} from '../../data-access/product-models';

interface Card {
  label: string;
  value: number | undefined;
  icon: IconName;
  accent: string;
  filter: ProductFilter;
}

/**
 * The three summary cards above the table (products, low stock, out of stock). Each card is also a shortcut: clicking
 * it asks for the matching filter. Presentational: it receives data through `input()` and reports clicks through
 * `output()` (an event the parent listens to); it calls no service.
 */
@Component({
  selector: 'app-inventory-summary',
  imports: [Icon],
  // Reserves the cards' space from the first frame (3 stacked cards on phones, one row from `sm`): on a slow
  // device the cards can paint a frame after the rest of the page, and nothing below may jump when they do (CLS).
  host: { class: 'block min-h-[calc(3*5.75rem+2rem)] sm:min-h-[5.75rem]' },
  template: `
    <section class="grid gap-4 sm:grid-cols-3" aria-label="Inventory summary">
      @for (card of cards(); track card.label) {
        <button
          type="button"
          class="card flex h-[5.75rem] items-center gap-4 px-5 text-left transition hover:-translate-y-0.5 hover:shadow-md hover:ring-slate-300"
          (click)="selectFilter.emit(card.filter)"
        >
          <span class="flex size-11 items-center justify-center rounded-lg" [class]="card.accent">
            <app-icon [name]="card.icon" class="size-5" />
          </span>
          <span>
            <span class="block text-sm text-slate-500">{{ card.label }}</span>
            @if (card.value === undefined) {
              <span class="mt-1 block h-7 w-12 animate-pulse rounded bg-slate-200"></span>
            } @else {
              <span class="block text-2xl font-semibold tabular-nums text-slate-900">{{
                card.value
              }}</span>
            }
          </span>
        </button>
      }
    </section>
  `,
})
export class InventorySummary {
  /** `null` while loading: the cards show placeholders. */
  readonly summary = input.required<Summary | null>();
  readonly selectFilter = output<ProductFilter>();

  /** A computed value: recalculated only when `summary` changes. */
  protected readonly cards = computed((): Card[] => {
    const summary = this.summary();
    return [
      {
        label: 'Products',
        value: summary?.totalProducts,
        icon: 'package',
        accent: 'bg-brand-50 text-brand-600',
        filter: { kind: 'all' },
      },
      {
        label: `Low stock (≤ ${LOW_STOCK_THRESHOLD})`,
        value: summary?.lowStock,
        icon: 'trendingDown',
        accent: 'bg-amber-50 text-amber-600',
        filter: LOW_STOCK_FILTER,
      },
      {
        label: 'Out of stock',
        value: summary?.outOfStock,
        icon: 'ban',
        accent: 'bg-red-50 text-red-600',
        filter: OUT_OF_STOCK_FILTER,
      },
    ];
  });
}
