import { TestBed } from '@angular/core/testing';
import { ProductFilter } from '../../data-access/product-models';
import { InventorySummary } from './inventory-summary';

describe('InventorySummary', () => {
  it('shows the counts, and each card asks for its filter', async () => {
    const fixture = TestBed.createComponent(InventorySummary);
    fixture.componentRef.setInput('summary', {
      totalProducts: 850024,
      lowStock: 11323,
      outOfStock: 7080,
    });
    const selected: ProductFilter[] = [];
    fixture.componentInstance.selectFilter.subscribe((filter) => selected.push(filter));
    await fixture.whenStable();
    const cards = (fixture.nativeElement as HTMLElement).querySelectorAll('button');

    cards[2].click();

    expect(cards[0].textContent).toContain('850024');
    expect(selected).toEqual([{ kind: 'stock', min: 0, max: 0 }]);
  });

  it('shows placeholders until the counts arrive', async () => {
    const fixture = TestBed.createComponent(InventorySummary);
    fixture.componentRef.setInput('summary', null);
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelectorAll('.animate-pulse').length).toBe(
      3,
    );
  });
});
