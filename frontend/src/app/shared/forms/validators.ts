// Form validators Angular does not ship. A validator receives a field (or group) and returns null when the value is
// fine, or an error object such as `{ integer: true }` that validation-message.ts turns into words.
import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/** Rounding noise allowed by maxDecimals: in binary floating point, 0.29 * 100 is 28.999999999999996, not 29. */
const FLOAT_TOLERANCE = 1e-6;

/** Rejects values made only of whitespace (Validators.required accepts "   "). */
export const notBlank: ValidatorFn = (control: AbstractControl): ValidationErrors | null =>
  typeof control.value === 'string' && control.value.length > 0 && control.value.trim() === ''
    ? { required: true }
    : null;

/** Whole numbers only (an empty field is left to `Validators.required`). */
export const integer: ValidatorFn = (control: AbstractControl): ValidationErrors | null =>
  control.value === null || control.value === '' || Number.isInteger(control.value)
    ? null
    : { integer: true };

/** At most `places` decimal places (e.g. 2 for a price). */
export function maxDecimals(places: number): ValidatorFn {
  const factor = 10 ** places;
  return (control: AbstractControl): ValidationErrors | null => {
    const value = control.value;
    if (typeof value !== 'number') {
      return null;
    }
    const scaled = value * factor;
    return Math.abs(Math.round(scaled) - scaled) < FLOAT_TOLERANCE
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
