import { TestBed } from '@angular/core/testing';
import * as faroSdk from '@grafana/faro-web-sdk';
import type { BrowserConfig, TransportItem } from '@grafana/faro-web-sdk';
import { fakeAuth } from '../../../testing/fake-auth';
import { BROWSER_ERRORS_CONFIG, BrowserErrors, FARO_SDK, withoutQuery } from './browser-errors';

describe('BrowserErrors', () => {
  let pushError: ReturnType<typeof vi.fn>;
  let faroConfig: BrowserConfig | undefined;
  let loadSdk: ReturnType<typeof vi.fn>;

  function setUp(enabled = true): BrowserErrors {
    TestBed.configureTestingModule({
      providers: [
        fakeAuth(),
        {
          provide: BROWSER_ERRORS_CONFIG,
          useValue: { enabled, url: '/collect', app: { name: 'web', version: '1.2.3' } },
        },
        { provide: FARO_SDK, useValue: loadSdk },
      ],
    });
    return TestBed.inject(BrowserErrors);
  }

  beforeEach(() => {
    pushError = vi.fn();
    faroConfig = undefined;
    // The real SDK, except that starting it records its configuration instead of patching the console.
    loadSdk = vi.fn(async () => ({
      ...faroSdk,
      initializeFaro: (config: BrowserConfig) => {
        faroConfig = config;
        return { api: { pushError } };
      },
    }));
  });

  it('reports errors to the same-origin collector once started, errors only', async () => {
    const browserErrors = setUp();
    await browserErrors.start();

    browserErrors.report(new Error('boom'));

    expect(pushError).toHaveBeenCalledWith(new Error('boom'));
    expect(faroConfig?.url).toBe('/collect');
    expect(faroConfig?.app).toEqual({ name: 'web', version: '1.2.3' });
    expect(faroConfig?.sessionTracking?.enabled).toBe(false);
    expect(faroConfig?.instrumentations?.map((instrumentation) => instrumentation.name)).toEqual([
      '@grafana/faro-web-sdk:instrumentation-console',
    ]);
  });

  it('keeps the errors raised before the SDK was loaded and sends them after', async () => {
    const browserErrors = setUp();

    browserErrors.report(new Error('early'));
    await browserErrors.start();

    expect(pushError).toHaveBeenCalledWith(new Error('early'));
  });

  it('sends the route without its query string (in the page and the stack), the user id and a short browser description', async () => {
    const browserErrors = setUp();
    await browserErrors.start();
    const item = {
      payload: {
        type: 'Error',
        value: 'boom',
        stacktrace: {
          frames: [{ filename: 'https://app.example.com/products?search=secret', function: 'f' }],
        },
      },
      meta: {
        page: { url: 'https://app.example.com/products?search=secret#top' },
        browser: {
          name: 'Chrome',
          version: '140',
          os: 'Mac OS 15',
          mobile: false,
          userAgent: 'Mozilla…',
        },
      },
    } as unknown as TransportItem;

    const sent = faroConfig?.beforeSend?.(item);

    expect(sent?.meta.page?.url).toBe('https://app.example.com/products');
    expect(JSON.stringify(sent?.payload)).not.toContain('secret');
    expect(sent?.meta.user).toEqual({ id: 'test-user' });
    expect(sent?.meta.browser).toEqual({
      name: 'Chrome',
      version: '140',
      os: 'Mac OS 15',
      mobile: false,
    });
  });

  it('never throws, whether the SDK fails to load or to send', async () => {
    const broken = setUp();
    loadSdk.mockRejectedValueOnce(new Error('chunk failed to load'));
    await expect(broken.start()).resolves.toBeUndefined();
    expect(() => broken.report(new Error('lost'))).not.toThrow();

    TestBed.resetTestingModule();
    const failing = setUp();
    await failing.start();
    pushError.mockImplementation(() => {
      throw new Error('transport failed');
    });
    expect(() => failing.report(new Error('boom'))).not.toThrow();
  });

  it('wraps values that are not errors, without sending the value itself', async () => {
    const browserErrors = setUp();
    await browserErrors.start();

    browserErrors.report({ password: 'hunter2' });

    expect(pushError).toHaveBeenCalledWith(new Error('Non-Error value thrown: [object Object]'));
  });

  it('is inert when disabled (dev mode: the dev server and unit tests)', async () => {
    const browserErrors = setUp(false);

    await browserErrors.start();
    browserErrors.report(new Error('boom'));

    expect(loadSdk).not.toHaveBeenCalled();
    expect(pushError).not.toHaveBeenCalled();
  });

  it('is disabled by default in dev mode', () => {
    expect(TestBed.inject(BROWSER_ERRORS_CONFIG).enabled).toBe(false);
  });

  it('strips query strings and fragments from URLs', () => {
    expect(withoutQuery('/products?page=2')).toBe('/products');
    expect(withoutQuery('/login#form')).toBe('/login');
    expect(withoutQuery('/products')).toBe('/products');
  });
});
