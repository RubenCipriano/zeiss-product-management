import { FormControl, FormGroup } from '@angular/forms';
import { integer, maxDecimals, notBlank, orderedRange } from './validators';

describe('validators', () => {
  describe('notBlank', () => {
    it.each([['Frame'], [' Frame '], [''], [null], [3]])('accepts %j', (value) => {
      expect(notBlank(new FormControl(value))).toBeNull();
    });

    it.each([[' '], ['\t\n ']])('rejects the whitespace-only %j as required', (value) => {
      expect(notBlank(new FormControl(value))).toEqual({ required: true });
    });
  });

  describe('integer', () => {
    it.each([[0], [42], [-3], [null], ['']])('accepts %j', (value) => {
      expect(integer(new FormControl(value))).toBeNull();
    });

    it.each([[1.5], [-0.1], [Number.NaN], ['abc']])('rejects %j', (value) => {
      expect(integer(new FormControl(value))).toEqual({ integer: true });
    });
  });

  describe('maxDecimals', () => {
    const twoPlaces = maxDecimals(2);

    it.each([[10], [10.5], [10.99], [0.1 + 0.2], [1_000_000.01], [null], ['10.999']])(
      'accepts %j with at most 2 decimal places (non-numbers are left to other validators)',
      (value) => {
        expect(twoPlaces(new FormControl(value))).toBeNull();
      },
    );

    it.each([[10.999], [0.001], [-1.234]])('rejects %j', (value) => {
      expect(twoPlaces(new FormControl(value))).toEqual({ maxDecimals: { places: 2 } });
    });

    it('allows no decimals at all with 0 places', () => {
      expect(maxDecimals(0)(new FormControl(3))).toBeNull();
      expect(maxDecimals(0)(new FormControl(3.5))).toEqual({ maxDecimals: { places: 0 } });
    });
  });

  describe('orderedRange', () => {
    const range = (min: number | null, max: number | null) =>
      new FormGroup({ min: new FormControl(min), max: new FormControl(max) });

    it.each([
      [0, 10],
      [5, 5],
      [null, 10],
      [10, null],
      [null, null],
    ])('accepts min %j and max %j', (min, max) => {
      expect(orderedRange(range(min, max))).toBeNull();
    });

    it('rejects a min above the max', () => {
      expect(orderedRange(range(20, 5))).toEqual({ orderedRange: true });
    });
  });
});
