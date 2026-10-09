import { Component, computed, input } from '@angular/core';

interface IconShape {
  paths: readonly string[];
  circles?: readonly (readonly [cx: number, cy: number, r: number])[];
}

/** Outline icons (24×24, stroke-based, shapes from the Lucide set, ISC licence). */
const ICONS = {
  plus: { paths: ['M5 12h14', 'M12 5v14'] },
  minus: { paths: ['M5 12h14'] },
  search: { paths: ['m21 21-4.3-4.3'], circles: [[11, 11, 8]] },
  refresh: {
    paths: [
      'M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8',
      'M21 3v5h-5',
      'M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16',
      'M8 16H3v5',
    ],
  },
  more: {
    paths: [],
    circles: [
      [12, 5, 1],
      [12, 12, 1],
      [12, 19, 1],
    ],
  },
  pencil: {
    paths: [
      'M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z',
      'm15 5 4 4',
    ],
  },
  trash: {
    paths: [
      'M3 6h18',
      'M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6',
      'M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2',
    ],
  },
  package: {
    paths: [
      'M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z',
      'M12 22V12',
      'm3.3 7 7.703 4.734a2 2 0 0 0 1.994 0L20.7 7',
      'm7.5 4.27 9 5.15',
    ],
  },
  x: { paths: ['M18 6 6 18', 'm6 6 12 12'] },
  alert: {
    paths: [
      'm21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3',
      'M12 9v4',
      'M12 17h.01',
    ],
  },
  check: { paths: ['m9 12 2 2 4-4'], circles: [[12, 12, 10]] },
  ban: { paths: ['m4.9 4.9 14.2 14.2'], circles: [[12, 12, 10]] },
  trendingDown: { paths: ['M16 17h6v-6', 'm22 17-8.5-8.5-5 5L2 7'] },
  arrowUpDown: { paths: ['m21 16-4 4-4-4', 'M17 20V4', 'm3 8 4-4 4 4', 'M7 4v16'] },
  history: {
    paths: ['M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8', 'M3 3v5h5', 'M12 7v5l4 2'],
  },
  inbox: {
    paths: [
      'M22 12h-6l-2 3h-4l-2-3H2',
      'M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z',
    ],
  },
  chevronLeft: { paths: ['m15 18-6-6 6-6'] },
  chevronRight: { paths: ['m9 18 6-6-6-6'] },
  chevronsLeft: { paths: ['m11 17-5-5 5-5', 'm18 17-5-5 5-5'] },
  chevronsRight: { paths: ['m6 17 5-5-5-5', 'm13 17 5-5-5-5'] },
} satisfies Record<string, IconShape>;

export type IconName = keyof typeof ICONS;

/**
 * Inline SVG icon: no icon font, no extra request. Defaults to 16px (see tailwind.css); size and colour come from
 * utility classes on the element (`class="size-5 text-red-600"`). Decorative (hidden from screen readers).
 */
@Component({
  selector: 'app-icon',
  template: `
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      stroke-linecap="round"
      stroke-linejoin="round"
      class="size-full"
      aria-hidden="true"
    >
      @for (path of shape().paths; track $index) {
        <path [attr.d]="path" />
      }
      @for (circle of shape().circles ?? []; track $index) {
        <circle [attr.cx]="circle[0]" [attr.cy]="circle[1]" [attr.r]="circle[2]" />
      }
    </svg>
  `,
})
export class Icon {
  readonly name = input.required<IconName>();

  protected readonly shape = computed<IconShape>(() => ICONS[this.name()]);
}
