import { Component, computed, input, output } from '@angular/core';
import { Icon } from '../icon/icon';

export interface PageChange {
  /** 1-based. */
  page: number;
  pageSize: number;
}

/** Server-side paging controls: page size, range summary and first/previous/next/last. */
@Component({
  selector: 'app-paginator',
  imports: [Icon],
  template: `
    <nav
      class="flex flex-wrap items-center justify-between gap-3 border-t border-slate-200 px-4 py-3 text-sm text-slate-600"
      aria-label="Pagination"
    >
      <label class="flex items-center gap-2">
        Rows per page
        <select
          class="input w-auto py-1.5 pr-8"
          [value]="pageSize()"
          (change)="changeSize($any($event.target).value)"
          aria-label="Rows per page"
        >
          @for (size of pageSizeOptions(); track size) {
            <option [value]="size">{{ size }}</option>
          }
        </select>
      </label>

      <div class="flex items-center gap-3">
        <span class="tabular-nums" aria-live="polite">{{ rangeLabel() }}</span>
        <div class="flex items-center gap-1">
          <button
            type="button"
            class="btn-icon"
            aria-label="First page"
            [disabled]="!hasPrevious()"
            (click)="go(1)"
          >
            <app-icon name="chevronsLeft" />
          </button>
          <button
            type="button"
            class="btn-icon"
            aria-label="Previous page"
            [disabled]="!hasPrevious()"
            (click)="go(page() - 1)"
          >
            <app-icon name="chevronLeft" />
          </button>
          <button
            type="button"
            class="btn-icon"
            aria-label="Next page"
            [disabled]="!hasNext()"
            (click)="go(page() + 1)"
          >
            <app-icon name="chevronRight" />
          </button>
          <button
            type="button"
            class="btn-icon"
            aria-label="Last page"
            [disabled]="!hasNext()"
            (click)="go(lastPage())"
          >
            <app-icon name="chevronsRight" />
          </button>
        </div>
      </div>
    </nav>
  `,
})
export class Paginator {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly totalCount = input.required<number>();
  readonly pageSizeOptions = input<readonly number[]>([10, 20, 50, 100]);

  readonly pageChange = output<PageChange>();

  protected readonly lastPage = computed(() =>
    Math.max(1, Math.ceil(this.totalCount() / this.pageSize())),
  );
  protected readonly hasPrevious = computed(() => this.page() > 1);
  protected readonly hasNext = computed(() => this.page() < this.lastPage());
  protected readonly rangeLabel = computed(() => {
    const total = this.totalCount();
    if (total === 0) {
      return '0 of 0';
    }
    const first = (this.page() - 1) * this.pageSize() + 1;
    const last = Math.min(this.page() * this.pageSize(), total);
    return `${first}–${last} of ${total}`;
  });

  protected go(page: number): void {
    this.pageChange.emit({ page, pageSize: this.pageSize() });
  }

  protected changeSize(value: string): void {
    // A new page size restarts from the first page: the old page number may not exist any more.
    this.pageChange.emit({ page: 1, pageSize: Number(value) });
  }
}
