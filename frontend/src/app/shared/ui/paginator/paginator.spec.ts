import { TestBed } from '@angular/core/testing';
import { PageChange, Paginator } from './paginator';

describe('Paginator', () => {
  async function render(page: number, totalCount: number, pageSize = 20) {
    const fixture = TestBed.createComponent(Paginator);
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('pageSize', pageSize);
    fixture.componentRef.setInput('totalCount', totalCount);
    const changes: PageChange[] = [];
    fixture.componentInstance.pageChange.subscribe((change) => changes.push(change));
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const button = (label: string) =>
      element.querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`)!;
    return { element, changes, button };
  }

  it('summarises the visible range', async () => {
    const { element } = await render(2, 45);

    expect(element.textContent).toContain('21–40 of 45');
  });

  it('disables navigation past the first and last pages', async () => {
    const first = await render(1, 45);
    expect(first.button('Previous page').disabled).toBe(true);
    expect(first.button('Next page').disabled).toBe(false);

    TestBed.resetTestingModule();
    const last = await render(3, 45);
    expect(last.button('Next page').disabled).toBe(true);
    expect(last.button('Last page').disabled).toBe(true);
  });

  it('emits the requested page and restarts from page 1 when the size changes', async () => {
    const { element, changes, button } = await render(2, 45);

    button('Last page').click();
    const select = element.querySelector('select')!;
    select.value = '50';
    select.dispatchEvent(new Event('change'));

    expect(changes).toEqual([
      { page: 3, pageSize: 20 },
      { page: 1, pageSize: 50 },
    ]);
  });
});
