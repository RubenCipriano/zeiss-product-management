import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/** Rejects values made only of whitespace (Validators.required accepts "   "). */
export const notBlank: ValidatorFn = (control: AbstractControl): ValidationErrors | null =>
  typeof control.value === 'string' && control.value.length > 0 && control.value.trim() === ''
    ? { required: true }
    : null;

export const integer: ValidatorFn = (control: AbstractControl): ValidationErrors | null =>
  control.value === null || control.value === '' || Number.isInteger(control.value)
    ? null
    : { integer: true };

export function maxDecimals(places: number): ValidatorFn {
  const factor = 10 ** places;
  return (control: AbstractControl): ValidationErrors | null => {
    const value = control.value;
    if (typeof value !== 'number') {
      return null;
    }
    // Compare scaled values with a small tolerance to absorb binary floating point noise.
    return Math.abs(Math.round(value * factor) - value * factor) < 1e-6
      ? null
      : { maxDecimals: { places } };
  };
}

/** Group validator for a `{ min, max }` range: when both are set, `min` must not exceed `max`. */
export const orderedRange: ValidatorFn = (group: AbstractControl): ValidationErrors | null => {
  const min = group.get('min')?.value;
  const max = group.get('max')?.value;
  return typeof min === 'number' && typeof max === 'number' && min > max
    ? { orderedRange: true }
    : null;
};
