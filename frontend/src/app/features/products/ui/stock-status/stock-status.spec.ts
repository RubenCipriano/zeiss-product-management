import { TestBed } from '@angular/core/testing';
import { StockStatus } from './stock-status';

describe('StockStatus', () => {
  async function render(quantity: number): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(StockStatus);
    fixture.componentRef.setInput('quantity', quantity);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it.each([
    [0, 'Out of stock', 'stock-status--out'],
    [1, 'Low stock', 'stock-status--low'],
    [10, 'Low stock', 'stock-status--low'],
    [11, 'In stock', 'stock-status--ok'],
  ])('shows %i units as "%s"', async (quantity, label, cssClass) => {
    const element = await render(quantity);

    expect(element.textContent).toContain(String(quantity));
    expect(element.textContent).toContain(label);
    expect(element.classList).toContain(cssClass);
    expect(element.getAttribute('aria-label')).toBe(`${quantity} units, ${label}`);
  });
});
