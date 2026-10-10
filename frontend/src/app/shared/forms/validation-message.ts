import { ValidationErrors } from '@angular/forms';

/**
 * Turns a form field's errors into one message for the user ("Price must be at least 0.01."). Messages from the API
 * (key `server`) are shown as-is, so the API remains the source of truth for business rules.
 */
export function validationMessage(errors: ValidationErrors | null, label: string): string | null {
  if (!errors) {
    return null;
  }
  if (errors['server']) {
    return errors['server'] as string;
  }
  if (errors['required']) {
    return `${label} is required.`;
  }
  if (errors['maxlength']) {
    return `${label} must be at most ${errors['maxlength'].requiredLength} characters.`;
  }
  if (errors['min']) {
    return `${label} must be at least ${errors['min'].min}.`;
  }
  if (errors['max']) {
    return `${label} must be at most ${errors['max'].max}.`;
  }
  if (errors['integer']) {
    return `${label} must be a whole number.`;
  }
  if (errors['maxDecimals']) {
    return `${label} can have at most ${errors['maxDecimals'].places} decimal places.`;
  }
  return `${label} is invalid.`;
}
