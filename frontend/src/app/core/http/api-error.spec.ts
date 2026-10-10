import { HttpErrorResponse } from '@angular/common/http';
import { ApiError } from './api-error';

describe('ApiError', () => {
  const failed = (status: number, error: unknown = null) =>
    ApiError.fromHttpErrorResponse(new HttpErrorResponse({ status, error }));

  describe('fromHttpErrorResponse', () => {
    it('keeps the problem details: code, message, field errors and extensions', () => {
      const problem = {
        title: 'Conflict',
        status: 409,
        detail: 'Only 2 available.',
        errorCode: 'product.insufficient_stock',
        errors: { quantity: ['Too many.'] },
        available: 2,
      };

      const error = failed(409, problem);

      expect(error).toBeInstanceOf(Error);
      expect(error.name).toBe('ApiError');
      expect(error.status).toBe(409);
      expect(error.code).toBe('product.insufficient_stock');
      expect(error.message).toBe('Only 2 available.');
      expect(error.fieldErrors).toEqual({ quantity: ['Too many.'] });
      expect(error.problem).toEqual(problem);
    });

    it('falls back to the title, and to an http_<status> code', () => {
      const error = failed(403, { title: 'Forbidden' });

      expect(error.code).toBe('http_403');
      expect(error.message).toBe('Forbidden');
      expect(error.fieldErrors).toEqual({});
    });

    it.each([
      [500, 'Something went wrong on the server. Please try again later.'],
      [503, 'Something went wrong on the server. Please try again later.'],
      [404, 'The requested item no longer exists.'],
      [400, 'The request could not be completed.'],
    ])('gives a %i without problem details a generic message', (status, message) => {
      const error = failed(status, '<html>Error</html>');

      expect(error.code).toBe(`http_${status}`);
      expect(error.message).toBe(message);
      expect(error.problem).toBeNull();
    });

    it('reports an unreachable server as a network error', () => {
      const error = failed(0);

      expect(error.status).toBe(0);
      expect(error.code).toBe('network_error');
      expect(error.message).toContain('could not be reached');
    });
  });

  describe('fieldError', () => {
    const withErrors = (errors: Record<string, string[]>) =>
      new ApiError(400, 'validation_failed', 'Invalid', errors);

    it.each([['stockQuantity'], ['StockQuantity'], ['$.stockQuantity']])(
      'maps the server key %s to the stockQuantity form field',
      (key) => {
        expect(withErrors({ [key]: ['Must not be negative.'] }).fieldError('stockQuantity')).toBe(
          'Must not be negative.',
        );
      },
    );

    it("returns the field's first message", () => {
      expect(withErrors({ name: ['Is required.', 'Is too long.'] }).fieldError('name')).toBe(
        'Is required.',
      );
    });

    it('prefers the exact key when the field is named in several forms', () => {
      const error = withErrors({
        '$.price': ['Not a number.'],
        Price: ['Too high.'],
        price: ['Exact.'],
      });

      expect(error.fieldError('price')).toBe('Exact.');
    });

    it('returns null for a field without errors', () => {
      const error = withErrors({ name: ['Is required.'], '': ['Body missing.'] });

      expect(error.fieldError('price')).toBeNull();
      expect(withErrors({ price: [] }).fieldError('price')).toBeNull();
    });
  });
});
