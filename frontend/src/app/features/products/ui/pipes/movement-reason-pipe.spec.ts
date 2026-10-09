import { MovementReasonPipe } from './movement-reason-pipe';

describe('MovementReasonPipe', () => {
  const pipe = new MovementReasonPipe();

  it('gives each reason a label and badge classes', () => {
    expect(pipe.transform('StockRemoved')).toBe('Removed');
    expect(pipe.transform('StockRemoved', 'badge')).toContain('text-red-700');
    expect(pipe.transform('InitialStock')).toBe('Initial stock');
  });

  it('shows an unknown reason as it came from the API', () => {
    expect(pipe.transform('Recount' as never)).toBe('Recount');
  });
});
