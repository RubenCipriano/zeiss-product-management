import { TestBed } from '@angular/core/testing';
import { Notifications } from '../../../core/notifications/notifications';
import { ToastOutlet } from './toast-outlet';

describe('ToastOutlet', () => {
  async function render() {
    const fixture = TestBed.createComponent(ToastOutlet);
    const notifications = TestBed.inject(Notifications);
    await fixture.whenStable();
    return { fixture, notifications, element: fixture.nativeElement as HTMLElement };
  }

  it('announces its messages politely', async () => {
    const { element } = await render();

    expect(element.querySelector('[aria-live=polite]')).not.toBeNull();
  });

  it('shows successes as status messages and errors as alerts', async () => {
    const { fixture, notifications, element } = await render();

    notifications.success('Product saved.');
    notifications.error('The server could not be reached.');
    await fixture.whenStable();

    expect(element.querySelector('[role=status]')!.textContent).toContain('Product saved.');
    expect(element.querySelector('[role=alert]')!.textContent).toContain(
      'The server could not be reached.',
    );
  });

  it('dismisses a message from its button', async () => {
    const { fixture, notifications, element } = await render();
    notifications.success('Product saved.');
    notifications.success('Stock updated.');
    await fixture.whenStable();

    element.querySelector<HTMLButtonElement>('button[aria-label="Dismiss"]')!.click();
    await fixture.whenStable();

    expect(notifications.toasts().map((toast) => toast.message)).toEqual(['Stock updated.']);
    expect(element.textContent).not.toContain('Product saved.');
    expect(element.textContent).toContain('Stock updated.');
  });
});
