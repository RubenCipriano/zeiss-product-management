import { DialogRef } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { aProduct } from '../../../../testing/product-fixtures';
import { ApiError } from '../../../core/http/api-error';
import { Notifications } from '../../../core/notifications/notifications';
import { AppDialog, ConfirmDialog } from '../../../shared/ui';
import { Product } from '../data-access/product-models';
import { ProductsStore } from '../data-access/products-store';
import { ProductActions } from './product-actions';

describe('ProductActions', () => {
  const frame = aProduct({ id: 100002, name: 'Frame' });
  let store: { adjustStock: ReturnType<typeof vi.fn>; delete: ReturnType<typeof vi.fn> };
  let dialogs: { open: ReturnType<typeof vi.fn> };
  let notifications: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> };
  let actions: ProductActions;

  beforeEach(() => {
    store = { adjustStock: vi.fn(), delete: vi.fn() };
    dialogs = { open: vi.fn() };
    notifications = { success: vi.fn(), error: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        ProductActions,
        { provide: ProductsStore, useValue: store },
        { provide: AppDialog, useValue: dialogs },
        { provide: Notifications, useValue: notifications },
      ],
    });
    actions = TestBed.inject(ProductActions);
  });

  it('marks a product as pending during a quick change, with a new idempotency key per click', () => {
    const response = new Subject<Product>();
    store.adjustStock.mockReturnValue(response);

    actions.quickChange(frame, 'add', 10);
    expect(actions.pendingIds().has(frame.id)).toBe(true);
    response.next({ ...frame, stockQuantity: 22 });
    response.complete();

    expect(actions.pendingIds().has(frame.id)).toBe(false);
    expect(store.adjustStock).toHaveBeenCalledWith(frame, 'add', 10, expect.any(String));
  });

  it("shows the API's explanation when a quick change is refused", () => {
    store.adjustStock.mockReturnValue(
      throwError(() => new ApiError(409, 'product.insufficient_stock', 'Only 2 available.')),
    );

    actions.quickChange(frame, 'remove', 5);

    expect(notifications.error).toHaveBeenCalledWith('Only 2 available.');
    expect(actions.pendingIds().size).toBe(0);
  });

  it('deletes only after confirmation, naming the confirmation dialog as an alert', () => {
    dialogs.open.mockReturnValue({ closed: of(true) } as unknown as DialogRef);
    store.delete.mockReturnValue(of(undefined));

    actions.delete(frame);

    expect(dialogs.open).toHaveBeenCalledWith(
      ConfirmDialog,
      expect.objectContaining({ labelledBy: ConfirmDialog.TITLE_ID, role: 'alertdialog' }),
    );
    expect(store.delete).toHaveBeenCalledWith(frame);
    expect(notifications.success).toHaveBeenCalledWith('Deleted "Frame".');
  });

  it('does nothing when the deletion is cancelled', () => {
    dialogs.open.mockReturnValue({ closed: of(false) } as unknown as DialogRef);

    actions.delete(frame);

    expect(store.delete).not.toHaveBeenCalled();
  });
});
