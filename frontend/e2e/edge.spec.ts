import { expect, test } from '@playwright/test';

test.describe('the edge (gateway)', () => {
  test('sends a strict Content-Security-Policy and the other security headers', async ({
    request,
  }) => {
    const response = await request.get('/login');
    const headers = response.headers();

    expect(headers['content-security-policy']).toContain("script-src 'self'");
    expect(headers['content-security-policy']).toContain("frame-ancestors 'none'");
    expect(headers['x-content-type-options']).toBe('nosniff');
    expect(headers['permissions-policy']).toContain('camera=()');
  });

  test('refuses API calls without a valid token, before they reach the API', async ({
    request,
  }) => {
    const missing = await request.get('/api/products');
    const forged = await request.get('/api/products', {
      headers: { Authorization: 'Bearer eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.' },
    });

    for (const response of [missing, forged]) {
      expect(response.status()).toBe(401);
      expect(response.headers()['content-type']).toContain('application/problem+json');
      expect((await response.json()).errorCode).toBe('invalid_token');
    }
  });

  test('a missing file is a 404, and robots are told to stay out', async ({ request }) => {
    expect((await request.get('/no-such-file.json')).status()).toBe(404);
    expect(await (await request.get('/robots.txt')).text()).toContain('Disallow: /');
  });
});
