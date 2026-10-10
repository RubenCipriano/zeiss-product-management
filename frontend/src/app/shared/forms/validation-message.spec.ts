import { validationMessage } from './validation-message';

describe('validationMessage', () => {
  it('has nothing to say about a valid control', () => {
    expect(validationMessage(null, 'Price')).toBeNull();
  });

  it.each([
    [{ required: true }, 'Price is required.'],
    [
      { maxlength: { requiredLength: 200, actualLength: 201 } },
      'Price must be at most 200 characters.',
    ],
    [{ min: { min: 0.01, actual: 0 } }, 'Price must be at least 0.01.'],
    [{ max: { max: 1000000, actual: 1000001 } }, 'Price must be at most 1000000.'],
    [{ integer: true }, 'Price must be a whole number.'],
    [{ maxDecimals: { places: 2 } }, 'Price can have at most 2 decimal places.'],
    [{ somethingElse: true }, 'Price is invalid.'],
  ])('explains %j', (errors, message) => {
    expect(validationMessage(errors, 'Price')).toBe(message);
  });

  it("shows the server's message as-is, ahead of any client-side error", () => {
    expect(
      validationMessage({ required: true, server: 'Category 1 does not exist.' }, 'Category'),
    ).toBe('Category 1 does not exist.');
  });

  it('reports one message at a time, in a fixed order', () => {
    expect(validationMessage({ integer: true, min: { min: 0, actual: -1.5 } }, 'Stock')).toBe(
      'Stock must be at least 0.',
    );
  });
});
