import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';
import { ConfirmDialog } from './confirm-dialog';

describe('ConfirmDialog', () => {
  async function render() {
    const dialogRef = { close: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        {
          provide: DIALOG_DATA,
          useValue: { title: 'Delete product?', message: 'Gone for good.', confirmLabel: 'Delete' },
        },
        { provide: DialogRef, useValue: dialogRef },
      ],
    });
    const fixture = TestBed.createComponent(ConfirmDialog);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const button = (text: string) =>
      [...element.querySelectorAll('button')].find((b) => b.textContent?.trim() === text)!;
    return { element, dialogRef, button };
  }

  it('renders the question and confirms with true', async () => {
    const { element, dialogRef, button } = await render();

    expect(element.textContent).toContain('Delete product?');
    expect(element.textContent).toContain('Gone for good.');
    button('Delete').click();

    expect(dialogRef.close).toHaveBeenCalledWith(true);
  });

  it('cancels with false', async () => {
    const { dialogRef, button } = await render();

    button('Cancel').click();

    expect(dialogRef.close).toHaveBeenCalledWith(false);
  });
});
