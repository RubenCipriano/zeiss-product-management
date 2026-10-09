import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { aProduct } from '../../../../testing/product-fixtures';
import { ProductsApi } from '../data-access/products-api';
import { StockHistoryDialog } from './stock-history-dialog';

describe('StockHistoryDialog', () => {
  it("lists the product's movements with their sign, reason and author", async () => {
    const stockMovements = vi.fn().mockReturnValue(
      of({
        page: 1,
        pageSize: 10,
        totalPages: 1,
        items: [
          {
            id: 2,
            quantityChange: -3,
            stockAfter: 7,
            reason: 'StockRemoved',
            performedBy: 'clerk',
            occurredAt: '2026-10-09T10:00:00Z',
          },
          {
            id: 1,
            quantityChange: 10,
            stockAfter: 10,
            reason: 'InitialStock',
            performedBy: 'manager',
            occurredAt: '2026-10-08T10:00:00Z',
          },
        ],
        totalCount: 2,
      }),
    );
    TestBed.configureTestingModule({
      providers: [
        { provide: DIALOG_DATA, useValue: { product: aProduct({ id: 100004 }) } },
        { provide: DialogRef, useValue: { close: vi.fn() } },
        { provide: ProductsApi, useValue: { stockMovements } },
      ],
    });

    const fixture = TestBed.createComponent(StockHistoryDialog);
    await fixture.whenStable();
    const rows = (fixture.nativeElement as HTMLElement).querySelectorAll(
      '[data-testid=movement-row]',
    );

    expect(stockMovements).toHaveBeenCalledWith(100004, 1, 10);
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Removed');
    expect(rows[0].textContent).toContain('-3');
    expect(rows[0].textContent).toContain('clerk');
    expect(rows[1].textContent).toContain('+10');
  });
});
