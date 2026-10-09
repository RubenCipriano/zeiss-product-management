import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { ConfirmDialog } from './confirm-dialog';

describe('ConfirmDialog', () => {
  it('renders the question and confirms with true', async () => {
    const dialogRef = { close: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        {
          provide: MAT_DIALOG_DATA,
          useValue: { title: 'Delete product?', message: 'Gone for good.', confirmLabel: 'Delete' },
        },
        { provide: MatDialogRef, useValue: dialogRef },
      ],
    });
    const fixture = TestBed.createComponent(ConfirmDialog);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.textContent).toContain('Delete product?');
    expect(element.textContent).toContain('Gone for good.');

    const confirm = [...element.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Delete'),
    );
    confirm!.click();

    expect(dialogRef.close).toHaveBeenCalledWith(true);
  });
});
