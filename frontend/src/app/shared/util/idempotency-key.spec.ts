import { newIdempotencyKey } from './idempotency-key';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('newIdempotencyKey', () => {
  afterEach(() => vi.restoreAllMocks());

  it('uses crypto.randomUUID where the browser has it', () => {
    vi.spyOn(crypto, 'randomUUID').mockReturnValue('11111111-2222-4333-8444-555555555555');

    expect(newIdempotencyKey()).toBe('11111111-2222-4333-8444-555555555555');
  });

  it('builds a random UUID v4 without it (plain HTTP on a server address)', () => {
    vi.stubGlobal('crypto', { getRandomValues: crypto.getRandomValues.bind(crypto) });
    try {
      const first = newIdempotencyKey();
      const second = newIdempotencyKey();

      expect(first).toMatch(UUID_V4);
      expect(second).toMatch(UUID_V4);
      expect(first).not.toBe(second);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
