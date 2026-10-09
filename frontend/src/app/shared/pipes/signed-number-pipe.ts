import { inject, LOCALE_ID, Pipe, PipeTransform } from '@angular/core';

/** A change as people read it: `+10`, `-3`, `0`. Localised digits and grouping (`+1,200`). */
@Pipe({ name: 'signedNumber' })
export class SignedNumberPipe implements PipeTransform {
  private readonly format = new Intl.NumberFormat(inject(LOCALE_ID), { signDisplay: 'exceptZero' });

  transform(value: number | null | undefined): string {
    return value === null || value === undefined ? '' : this.format.format(value);
  }
}
