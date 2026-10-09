import { TestBed } from '@angular/core/testing';
import { SignedNumberPipe } from './signed-number-pipe';

describe('SignedNumberPipe', () => {
  it.each([
    [10, '+10'],
    [-3, '-3'],
    [0, '0'],
    [1200, '+1,200'],
    [null, ''],
  ])('formats %s as "%s"', (value, expected) => {
    const pipe = TestBed.runInInjectionContext(() => new SignedNumberPipe());
    expect(pipe.transform(value)).toBe(expected);
  });
});
