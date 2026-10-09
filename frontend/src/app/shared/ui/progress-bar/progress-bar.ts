import { Component, input } from '@angular/core';

/**
 * A thin indeterminate progress bar for "something is loading here", without hiding what is already on screen.
 * The space is always reserved, so showing it never shifts the layout.
 */
@Component({
  selector: 'app-progress-bar',
  host: { class: 'block h-0.5 overflow-hidden bg-slate-100' },
  template: `
    @if (active()) {
      <div
        role="progressbar"
        [attr.aria-label]="label()"
        class="h-full w-1/3 bg-brand-500 motion-safe:animate-progress-slide motion-reduce:w-full motion-reduce:animate-pulse"
      ></div>
    }
  `,
})
export class ProgressBar {
  readonly active = input.required<boolean>();
  readonly label = input('Loading');
}
