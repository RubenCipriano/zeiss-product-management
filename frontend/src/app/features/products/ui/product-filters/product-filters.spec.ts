import { TestBed } from '@angular/core/testing';
import { ProductFilter } from '../../data-access/product-models';
import { ProductFilters } from './product-filters';

describe('ProductFilters', () => {
  afterEach(() => vi.useRealTimers());

  async function render(filter: ProductFilter) {
    const fixture = TestBed.createComponent(ProductFilters);
    fixture.componentRef.setInput('filter', filter);
    const emitted: ProductFilter[] = [];
    fixture.componentInstance.filterChange.subscribe((value) => emitted.push(value));
    await fixture.whenStable();
    // Only now: the debounce is what is under test, and rendering must not wait on fake timers.
    vi.useFakeTimers();
    return { fixture, element: fixture.nativeElement as HTMLElement, emitted };
  }

  it('reports a name search once the user stops typing', async () => {
    const { element, emitted } = await render({ kind: 'name', name: '' });
    const input = element.querySelector<HTMLInputElement>('input[aria-label="Name contains"]')!;

    input.value = 'len';
    input.dispatchEvent(new Event('input'));
    input.value = 'lens ';
    input.dispatchEvent(new Event('input'));
    vi.advanceTimersByTime(300);

    expect(emitted).toEqual([{ kind: 'name', name: 'lens' }]);
  });

  it('follows the current filter without echoing it back (a summary card was clicked)', async () => {
    const { fixture, element, emitted } = await render({ kind: 'all' });

    fixture.componentRef.setInput('filter', { kind: 'stock', min: 0, max: 10 });
    fixture.detectChanges();
    vi.advanceTimersByTime(300);

    expect(element.querySelector<HTMLInputElement>('input[aria-label="Max stock"]')!.value).toBe(
      '10',
    );
    expect(element.querySelector('[aria-checked="true"]')!.textContent).toContain('Stock level');
    expect(emitted).toEqual([]);
  });

  it('never reports an invalid stock range', async () => {
    const { element, emitted } = await render({ kind: 'stock', min: null, max: null });
    const min = element.querySelector<HTMLInputElement>('input[aria-label="Min stock"]')!;
    const max = element.querySelector<HTMLInputElement>('input[aria-label="Max stock"]')!;

    min.value = '20';
    min.dispatchEvent(new Event('input'));
    max.value = '5';
    max.dispatchEvent(new Event('input'));
    vi.advanceTimersByTime(300);

    expect(emitted).toEqual([]);
  });

  it('switches filter kind from the tabs', async () => {
    const { element, emitted } = await render({ kind: 'name', name: 'lens' });

    [...element.querySelectorAll<HTMLButtonElement>('[role=radio]')]
      .find((b) => b.textContent!.includes('All'))!
      .click();

    expect(emitted).toEqual([{ kind: 'all' }]);
  });
});
