import { Component, effect, input, output } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { debounceTime, distinctUntilChanged, filter, map } from 'rxjs';
import { integer, orderedRange } from '../../../../shared/forms/validators';
import { Icon } from '../../../../shared/ui';
import {
  LOW_STOCK_THRESHOLD,
  ProductFilter,
  ProductFilterKind,
} from '../../data-access/product-models';

const DEBOUNCE_MS = 300;

/**
 * Filter bar: all products, search by name, or a stock range. Controlled component: `filter` (the store's current
 * filter) is the single source of truth and the form follows it; user input is reported, debounced, through
 * `filterChange`.
 */
@Component({
  selector: 'app-product-filters',
  imports: [ReactiveFormsModule, Icon],
  templateUrl: './product-filters.html',
})
export class ProductFilters {
  readonly filter = input.required<ProductFilter>();
  readonly filterChange = output<ProductFilter>();

  protected readonly lowStockThreshold = LOW_STOCK_THRESHOLD;
  protected readonly options: readonly { kind: ProductFilterKind; label: string }[] = [
    { kind: 'all', label: 'All products' },
    { kind: 'name', label: 'Search by name' },
    { kind: 'stock', label: 'Stock level' },
  ];

  protected readonly name = new FormControl('', { nonNullable: true });
  protected readonly stockRange = new FormGroup(
    {
      min: new FormControl<number | null>(null, [Validators.min(0), integer]),
      max: new FormControl<number | null>(null, [Validators.min(0), integer]),
    },
    { validators: orderedRange },
  );

  constructor() {
    // Follow the current filter (e.g. a summary card was clicked) without echoing it back.
    effect(() => {
      const current = this.filter();
      if (current.kind === 'name' && current.name !== this.name.value.trim()) {
        this.name.setValue(current.name, { emitEvent: false });
      }
      if (current.kind === 'stock') {
        this.stockRange.setValue({ min: current.min, max: current.max }, { emitEvent: false });
      }
    });

    this.name.valueChanges
      .pipe(
        debounceTime(DEBOUNCE_MS),
        map((name) => name.trim()),
        distinctUntilChanged(),
        takeUntilDestroyed(),
      )
      .subscribe((name) => this.filterChange.emit({ kind: 'name', name }));

    this.stockRange.valueChanges
      .pipe(
        debounceTime(DEBOUNCE_MS),
        filter(() => this.stockRange.valid),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.filterChange.emit(this.stockFilter()));
  }

  protected select(kind: ProductFilterKind): void {
    switch (kind) {
      case 'all':
        this.filterChange.emit({ kind: 'all' });
        return;
      case 'name':
        this.filterChange.emit({ kind: 'name', name: this.name.value.trim() });
        return;
      case 'stock':
        this.filterChange.emit(this.stockFilter());
        return;
    }
  }

  protected setStockRange(min: number | null, max: number | null): void {
    this.stockRange.setValue({ min, max });
  }

  private stockFilter(): ProductFilter {
    const { min, max } = this.stockRange.getRawValue();
    return { kind: 'stock', min, max };
  }
}
