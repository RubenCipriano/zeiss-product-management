import { randomUUID } from 'node:crypto';
import { expect, signIn, test } from './support';

/**
 * Browser errors reach Loki: the page's error is reported by the Faro SDK to the gateway's /collect, forwarded to
 * Alloy in the cluster and stored in Loki on the monitoring server, where this test reads it back. Needs that
 * monitoring server (LOKI_URL, by default the local one); skipped where there is none (the CI cluster).
 */
const lokiUrl = process.env['LOKI_URL'] ?? 'http://localhost:3100';

interface LokiStream {
  stream: Record<string, string>;
  values: [string, string][];
}

test.describe('browser errors', () => {
  test.beforeAll(async ({ request }) => {
    const ready = await request.get(`${lokiUrl}/ready`).catch(() => null);
    test.skip(!ready?.ok(), `no monitoring server at ${lokiUrl}`);
  });

  test('an uncaught error in the page is stored in Loki, with the route and the user but no query string', async ({
    page,
    request,
  }) => {
    const violations: string[] = [];
    page.on('console', (message) => {
      if (/Content Security Policy|Refused to/i.test(message.text())) {
        violations.push(message.text());
      }
    });
    await signIn(page, 'manager');
    await page.goto('/products?search=not-sent');
    await expect(page.getByTestId('product-row').first()).toBeVisible();
    const id = randomUUID();
    const started = Date.now();

    // A real uncaught error, thrown by the browser outside any Angular code path.
    const reported = page.waitForResponse(
      (response) => response.url().endsWith('/collect') && response.request().method() === 'POST',
    );
    await page.evaluate((message) => {
      setTimeout(() => {
        throw new Error(message);
      });
    }, `e2e browser error ${id}`);
    expect((await reported).ok(), 'the gateway and the collector accepted the report').toBe(true);

    const streams = async (): Promise<LokiStream[]> => {
      const response = await request.get(`${lokiUrl}/loki/api/v1/query_range`, {
        params: {
          query: `{namespace="browser"} |= "${id}"`,
          start: `${(started - 60_000) * 1_000_000}`,
          limit: '10',
        },
      });
      return ((await response.json()) as { data: { result: LokiStream[] } }).data.result;
    };
    await expect.poll(async () => (await streams()).length, { timeout: 30_000 }).toBeGreaterThan(0);

    const [stream] = await streams();
    const line = JSON.parse(stream.values[0][1]) as Record<string, string>;
    expect(stream.stream).toMatchObject({ app: 'browser', level: 'error', user: 'manager' });
    expect(stream.stream['page']).toMatch(/\/products$/);
    expect(line['value']).toBe(`e2e browser error ${id}`);
    expect(JSON.stringify(stream)).not.toContain('not-sent');
    expect(JSON.stringify(stream)).not.toContain('eyJ'); // no token
    expect(violations, 'CSP violations').toEqual([]);
  });
});
