import { TestBed } from '@angular/core/testing';
import { ProgressBar } from './progress-bar';

describe('ProgressBar', () => {
  it('announces progress only while active', async () => {
    const fixture = TestBed.createComponent(ProgressBar);
    fixture.componentRef.setInput('active', false);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[role=progressbar]')).toBeNull();

    fixture.componentRef.setInput('active', true);
    await fixture.whenStable();
    expect(element.querySelector('[role=progressbar]')!.getAttribute('aria-label')).toBe('Loading');
  });
});
