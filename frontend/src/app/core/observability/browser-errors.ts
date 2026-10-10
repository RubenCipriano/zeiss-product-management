// Reports the errors that happen in users' browsers to the log system (BrowserErrors), and its configuration.
import { inject, InjectionToken, isDevMode, Service } from '@angular/core';
import type { ExceptionStackFrame, Faro, TransportItem } from '@grafana/faro-web-sdk';
import { Auth } from '../auth/auth';

/** The release, set by `ng build --define APP_VERSION=...` (frontend/Dockerfile). Undefined in dev and unit tests. */
declare const APP_VERSION: string | undefined;

export interface BrowserErrorsConfig {
  /** Off in dev mode (the dev server and unit tests): nothing receives the reports there. */
  enabled: boolean;
  /** Same origin: the gateway forwards it to the collector in the cluster (Grafana Alloy), so CSP stays 'self'. */
  url: string;
  app: { name: string; version: string };
}

export const BROWSER_ERRORS_CONFIG = new InjectionToken<BrowserErrorsConfig>(
  'BROWSER_ERRORS_CONFIG',
  {
    factory: () => ({
      enabled: !isDevMode(),
      url: '/collect',
      app: {
        name: 'product-management-web',
        version: typeof APP_VERSION === 'string' ? APP_VERSION : 'dev',
      },
    }),
  },
);

/**
 * Loads the Grafana Faro SDK with a dynamic `import()`: a separate file, downloaded after the first render. An
 * InjectionToken (a named value Angular can inject) so that tests can pass a fake SDK.
 */
export const FARO_SDK = new InjectionToken<() => Promise<typeof import('@grafana/faro-web-sdk')>>(
  'FARO_SDK',
  { factory: () => () => import('@grafana/faro-web-sdk') },
);

/** Errors raised before the SDK has loaded are kept and sent once it has; a few are enough to diagnose. */
const MAX_QUEUED = 10;

/**
 * Sends the errors that happen in users' browsers to the log system (Loki), next to the gateway's and the API's logs,
 * so a developer can see them (the project guide, chapter 8 (observability)). Uses Grafana Faro, a small browser SDK, loaded after
 * the page has rendered.
 *
 * Errors only: whatever reaches Angular's ErrorHandler (ReportingErrorHandler) and `console.error`. No performance
 * timings, user actions or sessions. Sent with each error: its stack, the app's release, the route without its query
 * string, the browser's name/version/OS and the signed-in user's id; never the token, form values or the full URL.
 * Reporting can never break the app: every failure in it is swallowed.
 */
@Service()
export class BrowserErrors {
  private readonly config = inject(BROWSER_ERRORS_CONFIG);
  private readonly loadSdk = inject(FARO_SDK);
  private readonly auth = inject(Auth);

  private faro?: Faro;
  private started = false;
  private readonly queue: unknown[] = [];

  /** Loads and starts the SDK. Called once, after the first render, so it never delays the first paint. */
  async start(): Promise<void> {
    if (!this.config.enabled || this.started) {
      return;
    }
    this.started = true;
    try {
      const sdk = await this.loadSdk();
      const { LogLevel } = sdk;
      this.faro = sdk.initializeFaro({
        url: this.config.url,
        app: this.config.app,
        // console.error only: uncaught errors and rejections already arrive through Angular's error handler
        // (report below), so Faro's own ErrorsInstrumentation would send each one twice.
        instrumentations: [new sdk.ConsoleInstrumentation()],
        consoleInstrumentation: {
          disabledLevels: [
            LogLevel.TRACE,
            LogLevel.DEBUG,
            LogLevel.LOG,
            LogLevel.INFO,
            LogLevel.WARN,
          ],
        },
        // No session id kept in the browser's storage and no session events.
        sessionTracking: { enabled: false },
        preventGlobalExposure: true,
        beforeSend: (item) => this.withContext(item),
      });
      for (const error of this.queue.splice(0)) {
        this.send(error);
      }
    } catch {
      // The chunk could not be downloaded (offline, a release replaced it) or the SDK failed: no reports, same app.
      this.queue.length = 0;
    }
  }

  report(error: unknown): void {
    if (!this.config.enabled) {
      return;
    }
    if (!this.faro) {
      if (this.queue.length < MAX_QUEUED) {
        this.queue.push(error);
      }
      return;
    }
    this.send(error);
  }

  private send(error: unknown): void {
    try {
      this.faro?.api.pushError(toError(error));
    } catch {
      // Never let reporting an error raise another one.
    }
  }

  /** The context of every report, applied when it is sent, so it is current (user signed in or out since). */
  private withContext(item: TransportItem): TransportItem {
    const { page, browser } = item.meta;
    const user = this.auth.userName();
    const frames = stackFrames(item.payload);
    return {
      ...item,
      // A frame can point at the page itself (code run by the browser for the document), query string included.
      payload: frames
        ? {
            ...item.payload,
            stacktrace: {
              frames: frames.map((frame) => ({ ...frame, filename: withoutQuery(frame.filename) })),
            },
          }
        : item.payload,
      meta: {
        ...item.meta,
        // The route only: a query string or fragment can carry search terms or other personal data.
        page: page?.url ? { ...page, url: withoutQuery(page.url) } : page,
        // Enough to reproduce; the full user-agent string, language and viewport are left out.
        browser: browser && {
          name: browser.name,
          version: browser.version,
          os: browser.os,
          mobile: browser.mobile,
        },
        // The token's subject (e.g. "manager"); never the token itself.
        user: user ? { id: user } : undefined,
      },
    };
  }
}

export function withoutQuery(url: string): string {
  return url.split(/[?#]/, 1)[0];
}

function stackFrames(payload: unknown): ExceptionStackFrame[] | undefined {
  const frames = (payload as { stacktrace?: { frames?: ExceptionStackFrame[] } }).stacktrace
    ?.frames;
  return Array.isArray(frames) ? frames : undefined;
}

/** Faro reports Error objects; a rejection can carry anything. Its value is not sent: it may hold user data. */
function toError(error: unknown): Error {
  if (error instanceof Error) {
    return error;
  }
  return typeof error === 'string'
    ? new Error(error)
    : new Error(`Non-Error value thrown: ${Object.prototype.toString.call(error)}`);
}
